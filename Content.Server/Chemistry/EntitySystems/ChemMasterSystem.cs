using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Chemistry.Components;
using Content.Server.Labels;
using Content.Server.Labels.Components;
using Content.Server.Popups;
using Content.Server.Storage.Components;
using Content.Server.Storage.EntitySystems;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Utility;


namespace Content.Server.Chemistry.EntitySystems
{

    /// <summary>
    /// Contains all the server-side logic for ChemMasters.
    /// <seealso cref="ChemMasterComponent"/>
    /// </summary>
    [UsedImplicitly]
    public sealed class ChemMasterSystem : EntitySystem
    {
        [Dependency] private readonly PopupSystem _popupSystem = default!;
        [Dependency] private readonly AudioSystem _audioSystem = default!;
        [Dependency] private readonly SolutionContainerSystem _solutionContainerSystem = default!;
        [Dependency] private readonly ItemSlotsSystem _itemSlotsSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
        [Dependency] private readonly StorageSystem _storageSystem = default!;
        [Dependency] private readonly LabelSystem _labelSystem = default!;

        private const string PillPrototypeId = "Pill";

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<ChemMasterComponent, ComponentStartup>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<ChemMasterComponent, SolutionChangedEvent>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<ChemMasterComponent, EntInsertedIntoContainerMessage>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<ChemMasterComponent, EntRemovedFromContainerMessage>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<ChemMasterComponent, BoundUIOpenedEvent>((_, comp, _) => UpdateUiState(comp));

            SubscribeLocalEvent<ChemMasterComponent, ChemMasterSetModeMessage>(OnSetModeMessage);
            SubscribeLocalEvent<ChemMasterComponent, ChemMasterSetPillTypeMessage>(OnSetPillTypeMessage);
            SubscribeLocalEvent<ChemMasterComponent, ChemMasterReagentAmountButtonMessage>(OnReagentButtonMessage);
            SubscribeLocalEvent<ChemMasterComponent, ChemMasterCreatePillsMessage>(OnCreatePillsMessage);
            SubscribeLocalEvent<ChemMasterComponent, ChemMasterOutputToBottleMessage>(OnOutputToBottleMessage);
        }

        private void UpdateUiState(ChemMasterComponent chemMaster, bool updateLabel = false)
        {
            if (!_solutionContainerSystem.TryGetSolution(chemMaster.Owner, SharedChemMaster.BufferSolutionName, out var bufferSolution))
                return;
            var inputContainer = _itemSlotsSystem.GetItemOrNull(chemMaster.Owner, SharedChemMaster.InputSlotName);
            var outputContainer = _itemSlotsSystem.GetItemOrNull(chemMaster.Owner, SharedChemMaster.OutputSlotName);

            var bufferReagents = bufferSolution.Contents;
            var bufferCurrentVolume = bufferSolution.CurrentVolume;

            var state = new ChemMasterBoundUserInterfaceState(
                chemMaster.Mode, BuildInputContainerInfo(inputContainer), BuildOutputContainerInfo(outputContainer),
                bufferReagents, bufferCurrentVolume, chemMaster.PillType, chemMaster.PillDosageLimit, updateLabel);

            _userInterfaceSystem.TrySetUiState(chemMaster.Owner, ChemMasterUiKey.Key, state);
        }

        private void OnSetModeMessage(EntityUid uid, ChemMasterComponent chemMaster, ChemMasterSetModeMessage message)
        {
            // Ensure the mode is valid, either Transfer or Discard.
            if (!Enum.IsDefined(typeof(ChemMasterMode), message.ChemMasterMode))
                return;

            chemMaster.Mode = message.ChemMasterMode;
            UpdateUiState(chemMaster);
            ClickSound(chemMaster);
        }

        private void OnSetPillTypeMessage(EntityUid uid, ChemMasterComponent chemMaster, ChemMasterSetPillTypeMessage message)
        {
            // Ensure valid pill type. There are 20 pills selectable, 0-19.
            if (message.PillType > SharedChemMaster.PillTypes - 1)
                return;

            chemMaster.PillType = message.PillType;
            UpdateUiState(chemMaster);
            ClickSound(chemMaster);
        }

        private void OnReagentButtonMessage(EntityUid uid, ChemMasterComponent chemMaster, ChemMasterReagentAmountButtonMessage message)
        {
            // Ensure the amount corresponds to one of the reagent amount buttons.
            if (!Enum.IsDefined(typeof(ChemMasterReagentAmount), message.Amount))
                return;

            switch (chemMaster.Mode)
            {
                case ChemMasterMode.Transfer:
                    TransferReagents(chemMaster, message.ReagentId, message.Amount.GetFixedPoint(), message.FromBuffer);
                    break;
                case ChemMasterMode.Discard:
                    DiscardReagents(chemMaster, message.ReagentId, message.Amount.GetFixedPoint(), message.FromBuffer);
                    break;
                default:
                    // Invalid mode.
                    return;
            }

            ClickSound(chemMaster);
        }

        private void TransferReagents(ChemMasterComponent chemMaster, string reagentId, FixedPoint2 amount, bool fromBuffer)
        {
            var container = _itemSlotsSystem.GetItemOrNull(chemMaster.Owner, SharedChemMaster.InputSlotName);
            if (container is null ||
                !_solutionContainerSystem.TryGetFitsInDispenser(container.Value, out var containerSolution) ||
                !_solutionContainerSystem.TryGetSolution(chemMaster.Owner, SharedChemMaster.BufferSolutionName, out var bufferSolution))
            {
                return;
            }

            if (fromBuffer) // Buffer to container
            {
                amount = FixedPoint2.Min(amount, containerSolution.AvailableVolume);
                amount = bufferSolution.RemoveReagent(reagentId, amount);
                _solutionContainerSystem.TryAddReagent(container.Value, containerSolution, reagentId, amount, out var _);
            }
            else // Container to buffer
            {
                amount = FixedPoint2.Min(amount, containerSolution.GetReagentQuantity(reagentId));
                _solutionContainerSystem.TryRemoveReagent(container.Value, containerSolution, reagentId, amount);
                bufferSolution.AddReagent(reagentId, amount);
            }

            UpdateUiState(chemMaster, updateLabel: true);
        }

        private void DiscardReagents(ChemMasterComponent chemMaster, string reagentId, FixedPoint2 amount, bool fromBuffer)
        {

            if (fromBuffer)
            {
                if (_solutionContainerSystem.TryGetSolution(chemMaster.Owner, SharedChemMaster.BufferSolutionName, out var bufferSolution))
                    bufferSolution.RemoveReagent(reagentId, amount);
                else
                    return;
            }
            else
            {
                var container = _itemSlotsSystem.GetItemOrNull(chemMaster.Owner, SharedChemMaster.InputSlotName);
                if (container is not null &&
                    _solutionContainerSystem.TryGetFitsInDispenser(container.Value, out var containerSolution))
                {
                    _solutionContainerSystem.TryRemoveReagent(container.Value, containerSolution, reagentId, amount);
                }
                else
                    return;
            }

            UpdateUiState(chemMaster, updateLabel: fromBuffer);
        }

        private void OnCreatePillsMessage(EntityUid uid, ChemMasterComponent chemMaster, ChemMasterCreatePillsMessage message)
        {
            var user = message.Session.AttachedEntity;
            var maybeContainer = _itemSlotsSystem.GetItemOrNull(chemMaster.Owner, SharedChemMaster.OutputSlotName);
            if (maybeContainer is not { Valid: true } container
                || !TryComp(container, out ServerStorageComponent? storage)
                || storage.Storage is null)
            {
                return; // output can't fit pills
            }

            // Ensure the number is valid.
            if (message.Number == 0 || message.Number > storage.StorageCapacityMax - storage.StorageUsed)
                return;

            // Ensure the amount is valid.
            if (message.Dosage == 0 || message.Dosage > chemMaster.PillDosageLimit)
                return;

            var needed = message.Dosage * message.Number;
            if (!WithdrawFromBuffer(chemMaster, needed, user, out var withdrawal))
                return;

            _labelSystem.Label(container, message.Label);

            for (var i = 0; i < message.Number; i++)
            {
                var item = Spawn(PillPrototypeId, Transform(container).Coordinates);
                _storageSystem.Insert(container, item, storage);
                _labelSystem.Label(item, message.Label);

                var itemSolution = _solutionContainerSystem.EnsureSolution(item, SharedChemMaster.PillSolutionName);

                _solutionContainerSystem.TryAddSolution(
                    item, itemSolution, withdrawal.SplitSolution(message.Dosage));

                if (TryComp<SpriteComponent>(item, out var spriteComp))
                    spriteComp.LayerSetState(0, "pill" + (chemMaster.PillType + 1));
            }

            UpdateUiState(chemMaster);
            ClickSound(chemMaster);
        }

        private void OnOutputToBottleMessage(
            EntityUid uid, ChemMasterComponent chemMaster, ChemMasterOutputToBottleMessage message)
        {
            var user = message.Session.AttachedEntity;
            var maybeContainer = _itemSlotsSystem.GetItemOrNull(chemMaster.Owner, SharedChemMaster.OutputSlotName);
            if (maybeContainer is not { Valid: true } container
                || !_solutionContainerSystem.TryGetSolution(
                    container, SharedChemMaster.BottleSolutionName, out var solution))
            {
                return; // output can't fit reagents
            }

            // Ensure the amount is valid.
            if (message.Dosage == 0 || message.Dosage > solution.AvailableVolume)
                return;

            if (!WithdrawFromBuffer(chemMaster, message.Dosage, user, out var withdrawal))
                return;

            _labelSystem.Label(container, message.Label);
            _solutionContainerSystem.TryAddSolution(
                container, solution, withdrawal);

            UpdateUiState(chemMaster);
            ClickSound(chemMaster);
        }

        private bool WithdrawFromBuffer(
            IComponent chemMaster,
            FixedPoint2 neededVolume, EntityUid? user,
            [NotNullWhen(returnValue: true)] out Solution? outputSolution)
        {
            outputSolution = null;

            if (!_solutionContainerSystem.TryGetSolution(
                    chemMaster.Owner, SharedChemMaster.BufferSolutionName, out var solution))
            {
                return false;
            }

            var filter = user.HasValue ? Filter.Entities(user.Value) : Filter.Empty();
            if (solution.TotalVolume == 0)
            {
                _popupSystem.PopupCursor(Loc.GetString("chem-master-window-buffer-empty-text"), filter);
                return false;
            }

            // ReSharper disable once InvertIf
            if (neededVolume > solution.CurrentVolume)
            {
                _popupSystem.PopupCursor(Loc.GetString("chem-master-window-buffer-low-text"), filter);
                return false;
            }

            outputSolution = solution.SplitSolution(neededVolume);
            return true;
        }

        private void ClickSound(ChemMasterComponent chemMaster)
        {
            _audioSystem.Play(chemMaster.ClickSound, Filter.Pvs(chemMaster.Owner), chemMaster.Owner, AudioParams.Default.WithVolume(-2f));
        }

        private ContainerInfo? BuildInputContainerInfo(EntityUid? container)
        {
            if (container is not { Valid: true })
                return null;

            if (!TryComp(container, out FitsInDispenserComponent? fits)
                || !_solutionContainerSystem.TryGetSolution(container.Value, fits.Solution, out var solution))
            {
                return null;
            }

            return BuildContainerInfo(Name(container.Value), solution);
        }

        private ContainerInfo? BuildOutputContainerInfo(EntityUid? container)
        {
            if (container is not { Valid: true })
                return null;

            var name = Name(container.Value);
            {
                if (_solutionContainerSystem.TryGetSolution(
                        container.Value, SharedChemMaster.BottleSolutionName, out var solution))
                {
                    return BuildContainerInfo(name, solution);
                }
            }

            if (!TryComp(container, out ServerStorageComponent? storage))
                return null;

            var pills = storage.Storage?.ContainedEntities.Select(pill =>
            {
                _solutionContainerSystem.TryGetSolution(pill, SharedChemMaster.PillSolutionName, out var solution);
                var quantity = solution?.CurrentVolume ?? FixedPoint2.Zero;
                return (Name(pill), quantity);
            }).ToList();

            return pills is null
                ? null
                : new ContainerInfo(name, false, storage.StorageUsed, storage.StorageCapacityMax, pills);
        }

        private static ContainerInfo BuildContainerInfo(string name, Solution solution)
        {
            var reagents = solution.Contents
                .Select(reagent => (reagent.ReagentId, reagent.Quantity)).ToList();

            return new ContainerInfo(name, true, solution.CurrentVolume, solution.MaxVolume, reagents);
        }
    }
}
