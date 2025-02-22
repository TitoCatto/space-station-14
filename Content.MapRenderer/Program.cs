﻿#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.MapRenderer.Painters;
using Content.Server.Maps;
using Newtonsoft.Json;
using Robust.Shared.Prototypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace Content.MapRenderer
{
    internal class Program
    {
        private const string NoMapsChosenMessage = "No maps were chosen";
        private static readonly Func<string, string> ChosenMapIdNotIntMessage = id => $"The chosen id is not a valid integer: {id}";
        private static readonly Func<int, string> NoMapFoundWithIdMessage = id => $"No map found with chosen id: {id}";

        private static readonly MapPainter MapPainter = new();

        internal static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                await using var server = await PoolManager.GetServerClient();
                var mapIds = server.Pair.Server
                    .ResolveDependency<IPrototypeManager>()
                    .EnumeratePrototypes<GameMapPrototype>()
                    .Select(map => map.ID)
                    .ToArray();

                Array.Sort(mapIds);

                Console.WriteLine("Didn't specify any maps to paint, select one, multiple separated by commas or \"all\":");
                Console.WriteLine(string.Join('\n', mapIds.Select((id, i) => $"({i}): {id}")));
                var input = Console.ReadLine();
                if (input == null)
                {
                    Console.WriteLine(NoMapsChosenMessage);
                    return;
                }

                var selectedIds = new List<int>();
                if (input is "all" or "\"all\"")
                {
                    selectedIds = Enumerable.Range(0, mapIds.Length).ToList();
                }
                else
                {
                    var inputArray = input.Split(',');
                    if (inputArray.Length == 0)
                    {
                        Console.WriteLine(NoMapsChosenMessage);
                        return;
                    }

                    foreach (var idString in inputArray)
                    {
                        if (!int.TryParse(idString.Trim(), out var id))
                        {
                            Console.WriteLine(ChosenMapIdNotIntMessage(idString));
                            return;
                        }

                        selectedIds.Add(id);
                    }
                }

                var selectedMapPrototypes = new List<string>();
                foreach (var id in selectedIds)
                {
                    if (id < 0 || id >= mapIds.Length)
                    {
                        Console.WriteLine(NoMapFoundWithIdMessage(id));
                        return;
                    }

                    selectedMapPrototypes.Add(mapIds[id]);
                }

                var argsLength = args.Length;
                Array.Resize(ref args, argsLength + selectedMapPrototypes.Count);
                selectedMapPrototypes.CopyTo(args, argsLength);

                if (selectedMapPrototypes.Count == 0)
                {
                    Console.WriteLine(NoMapsChosenMessage);
                    return;
                }

                Console.WriteLine($"Selected maps: {string.Join(", ", selectedMapPrototypes)}");
            }

            if (!CommandLineArguments.TryParse(args, out var arguments))
                return;

            await Run(arguments);
        }

        private static async Task Run(CommandLineArguments arguments)
        {
            Console.WriteLine($"Creating images for {arguments.Maps.Count} maps");

            var mapNames = new List<string>();
            foreach (var map in arguments.Maps)
            {
                Console.WriteLine($"Painting map {map}");

                var mapViewerData = new MapViewerData
                {
                    Id = map,
                    Name = Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(map)
                };

                mapViewerData.ParallaxLayers.Add(LayerGroup.DefaultParallax());
                var directory = Path.Combine(arguments.OutputPath, map);
                Directory.CreateDirectory(directory);

                var i = 0;
                await foreach (var renderedGrid in MapPainter.Paint(map))
                {
                    var grid = renderedGrid.Image;
                    Directory.CreateDirectory(directory);

                    var fileName = Path.GetFileNameWithoutExtension(map);
                    var savePath = $"{directory}{Path.DirectorySeparatorChar}{fileName}-{i}.{arguments.Format.ToString()}";

                    Console.WriteLine($"Writing grid of size {grid.Width}x{grid.Height} to {savePath}");

                    switch (arguments.Format)
                    {
                        case OutputFormat.webp:
                            var encoder = new WebpEncoder
                            {
                                Method = WebpEncodingMethod.BestQuality,
                                FileFormat = WebpFileFormatType.Lossless,
                                TransparentColorMode = WebpTransparentColorMode.Preserve
                            };

                            await grid.SaveAsync(savePath, encoder);
                            break;

                        default:
                        case OutputFormat.png:
                            await grid.SaveAsPngAsync(savePath);
                            break;
                    }

                    grid.Dispose();

                    mapViewerData.Grids.Add(new GridLayer(renderedGrid,  Path.Combine(map, Path.GetFileName(savePath))));

                    mapNames.Add(fileName);
                    i++;
                }

                if (arguments.ExportViewerJson)
                {
                    var json = JsonConvert.SerializeObject(mapViewerData);
                    await File.WriteAllTextAsync(Path.Combine(arguments.OutputPath, map, "map.json"), json);
                }
            }

            var mapNamesString = $"[{string.Join(',', mapNames.Select(s => $"\"{s}\""))}]";
            Console.WriteLine($@"::set-output name=map_names::{mapNamesString}");
            Console.WriteLine($"Created {arguments.Maps.Count} map images.");
        }
    }
}
