using Content.Server.Access.Systems;
using Content.Server.Popups;
using Content.Server.Radio.EntitySystems;
using Content.Server._NF.Bank;
using Content.Server._NF.Shipyard.Components;
using Content.Server._NF.ShuttleRecords;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Ghost;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Content.Shared.Radio;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Server.Maps;
using Content.Shared.StationRecords;
using Content.Server.Chat.Systems;
using Content.Server.Mind;
using Content.Server.Preferences.Managers;
using Content.Server.StationRecords;
using Content.Server.StationRecords.Systems;
using Content.Shared.Database;
using Content.Shared.Preferences;
using Content.Server.Shuttles.Components;
using Content.Server._NF.Station.Components;
using System.Text.RegularExpressions;
using Content.Shared.UserInterface;
using Robust.Shared.Audio.Systems;
using Content.Shared.Access;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.ShuttleRecords;
using Content.Server.StationEvents.Components;
using Content.Shared.Forensics.Components;
using Robust.Server.Player;
using Robust.Shared.Timing;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IServerPreferencesManager _prefManager = default!;
    [Dependency] private readonly AccessSystem _accessSystem = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly IdCardSystem _idSystem = default!;
    [Dependency] private readonly StationRecordsSystem _records = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly ShuttleRecordsSystem _shuttleRecordsSystem = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;

    private static readonly Regex DeedRegex = new(@"\s*\([^()]*\)");

    public void InitializeConsole()
    {

    }

    private void OnPurchaseMessage(EntityUid shipyardConsoleUid, ShipyardConsoleComponent component, ShipyardConsolePurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        TryComp<IdCardComponent>(targetId, out var idCard);
        TryComp<ShipyardVoucherComponent>(targetId, out var voucher);
        if (idCard is null && voucher is null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        if (HasComp<ShuttleDeedComponent>(targetId))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-already-deeded"));
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        if (TryComp<AccessReaderComponent>(shipyardConsoleUid, out var accessReaderComponent) && !_access.IsAllowed(player, shipyardConsoleUid, accessReaderComponent))
        {
            ConsolePopup(player, Loc.GetString("comms-console-permission-denied"));
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        if (!_prototypeManager.TryIndex<VesselPrototype>(args.Vessel, out var vessel))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-vessel", ("vessel", args.Vessel)));
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        if (!GetAvailableShuttles(shipyardConsoleUid, targetId: targetId).available.Contains(vessel.ID))
        {
            PlayDenySound(player, shipyardConsoleUid, component);
            _adminLogger.Add(LogType.Action, LogImpact.Medium, $"{ToPrettyString(player):player} tried to purchase a vessel that was never available.");
            return;
        }

        var name = vessel.Name;
        if (vessel.Price <= 0)
            return;

        if (_station.GetOwningStation(shipyardConsoleUid) is not { Valid: true } station)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        if (!TryComp<BankAccountComponent>(player, out var bank))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-bank"));
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        // Keep track of whether or not a voucher was used.
        // TODO: voucher purchase should be done in a separate function.
        bool voucherUsed = false;
        if (voucher is not null)
        {
            if (voucher!.RedemptionsLeft <= 0)
            {
                ConsolePopup(player, Loc.GetString("shipyard-console-no-voucher-redemptions"));
                PlayDenySound(player, shipyardConsoleUid, component);
                if (voucher!.DestroyOnEmpty)
                {
                    QueueDel(targetId);
                }
                return;
            }
            else if (!voucher.UseAnywhere && voucher!.ConsoleType != (ShipyardConsoleUiKey)args.UiKey)
            {
                ConsolePopup(player, Loc.GetString("shipyard-console-invalid-voucher-type"));
                PlayDenySound(player, shipyardConsoleUid, component);
                return;
            }
            voucher.RedemptionsLeft--;
            voucherUsed = true;
        }
        else
        {
            if (!_bank.TryBankWithdraw(player, vessel.Price))
            {
                ConsolePopup(player, Loc.GetString("cargo-console-insufficient-funds", ("cost", vessel.Price)));
                PlayDenySound(player, shipyardConsoleUid, component);
                return;
            }
        }

        if (!TryPurchaseShuttle(station, vessel.ShuttlePath, out var shuttleUidOut))
        {
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }

        var shuttleUid = shuttleUidOut.Value;
        if (!TryComp<ShuttleComponent>(shuttleUid, out var shuttle))
        {
            PlayDenySound(player, shipyardConsoleUid, component);
            return;
        }
        EntityUid? shuttleStation = null;
        // setting up any stations if we have a matching game map prototype to allow late joins directly onto the vessel
        if (_prototypeManager.TryIndex<GameMapPrototype>(vessel.ID, out var stationProto))
        {
            List<EntityUid> gridUids = new()
            {
                shuttleUid
            };
            shuttleStation = _station.InitializeNewStation(stationProto.Stations[vessel.ID], gridUids);
            name = Name(shuttleStation.Value);

            var vesselInfo = EnsureComp<ExtraShuttleInformationComponent>(shuttleStation.Value);
            vesselInfo.Vessel = vessel.ID;
        }

        if (TryComp<AccessComponent>(targetId, out var newCap))
        {
            var newAccess = newCap.Tags.ToList();
            newAccess.AddRange(component.NewAccessLevels);
            _accessSystem.TrySetTags(targetId, newAccess, newCap);
        }

        var deedID = EnsureComp<ShuttleDeedComponent>(targetId);

        var shuttleOwner = Name(player).Trim();
        AssignShuttleDeedProperties((targetId, deedID), shuttleUid, name, shuttleOwner, voucherUsed);

        var deedShuttle = EnsureComp<ShuttleDeedComponent>(shuttleUid);
        AssignShuttleDeedProperties((shuttleUid, deedShuttle), shuttleUid, name, shuttleOwner, voucherUsed);

        if (!voucherUsed && component.NewJobTitle != null)
        {
            _idSystem.TryChangeJobTitle(targetId, Loc.GetString(component.NewJobTitle), idCard, player);
        }

        // The following block of code is entirely to do with trying to sanely handle moving records from station to station.
        // it is ass.
        // This probably shouldnt be messed with further until station records themselves become more robust
        // and not entirely dependent upon linking ID card entity to station records key lookups
        // its just bad

        var stationList = EntityQueryEnumerator<StationRecordsComponent>();

        if (TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage)
            && shuttleStation != null
            && keyStorage.Key != null)
        {
            bool recSuccess = false;
            while (stationList.MoveNext(out var stationUid, out var stationRecComp))
            {
                if (!_records.TryGetRecord<GeneralStationRecord>(keyStorage.Key.Value, out var record))
                    continue;

                //_records.RemoveRecord(keyStorage.Key.Value);
                _records.AddRecordEntry(shuttleStation.Value, record);
                recSuccess = true;
                break;
            }

            if (!recSuccess
                && _mind.TryGetMind(player, out var mindUid, out var mindComp)
                && mindComp.UserId != null
                && _prefManager.GetPreferences(mindComp.UserId.Value).SelectedCharacter is HumanoidCharacterProfile profile)
            {
                TryComp<FingerprintComponent>(player, out var fingerprintComponent);
                TryComp<DnaComponent>(player, out var dnaComponent);
                TryComp<StationRecordsComponent>(shuttleStation, out var stationRec);
                _records.CreateGeneralRecord(shuttleStation.Value, targetId, profile.Name, profile.Age, profile.Species, profile.Gender, $"Captain", fingerprintComponent!.Fingerprint, dnaComponent!.DNA, profile, stationRec!);
            }
        }
        _records.Synchronize(shuttleStation!.Value);
        _records.Synchronize(station);

        EntityManager.AddComponents(shuttleUid, vessel.AddComponents);

        // Ensure cleanup on ship sale
        EnsureComp<LinkedLifecycleGridParentComponent>(shuttleUid);

        var sellValue = 0;
        if (!voucherUsed)
        {
            // Get the price of the ship
            if (TryComp<ShuttleDeedComponent>(targetId, out var deed))
                sellValue = (int)_pricing.AppraiseGrid((EntityUid)(deed?.ShuttleUid!), LacksPreserveOnSaleComp);

            // Adjust for taxes
            sellValue = CalculateShipResaleValue((shipyardConsoleUid, component), sellValue);
        }

        SendPurchaseMessage(shipyardConsoleUid, player, name, component.ShipyardChannel, secret: false);
        if (component.SecretShipyardChannel is { } secretChannel)
            SendPurchaseMessage(shipyardConsoleUid, player, name, secretChannel, secret: true);

        PlayConfirmSound(player, shipyardConsoleUid, component);
        if (voucherUsed)
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to purchase shuttle {ToPrettyString(shuttleUid)} with a voucher via {ToPrettyString(shipyardConsoleUid)}");
        else
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to purchase shuttle {ToPrettyString(shuttleUid)} for {vessel.Price} credits via {ToPrettyString(shipyardConsoleUid)}");

        // Adding the record to the shuttle records system makes them eligible to be copied.
        // Can be set on the component of the shipyard.
        if (component.CanTransferDeed)
        {
            _shuttleRecordsSystem.AddRecord(
                new ShuttleRecord(
                    name: deedShuttle.ShuttleName ?? "",
                    suffix: deedShuttle.ShuttleNameSuffix ?? "",
                    ownerName: shuttleOwner,
                    entityUid: EntityManager.GetNetEntity(shuttleUid),
                    purchasedWithVoucher: voucherUsed,
                    purchasePrice: (uint)vessel.Price,
                    vesselPrototypeId: vessel.ID
                )
            );
        }

        RefreshState(shipyardConsoleUid, bank.Balance, true, name, sellValue, targetId, (ShipyardConsoleUiKey)args.UiKey, voucherUsed);
    }

    private void TryParseShuttleName(ShuttleDeedComponent deed, string name)
    {
        // The logic behind this is: if a name part fits the requirements, it is the required part. Otherwise it's the name.
        // This may cause problems but ONLY when renaming a ship. It will still display properly regardless of this.
        var nameParts = name.Split(' ');

        var hasSuffix = nameParts.Length > 1 && nameParts.Last().Length < ShuttleDeedComponent.MaxSuffixLength && nameParts.Last().Contains('-');
        deed.ShuttleNameSuffix = hasSuffix ? nameParts.Last() : null;
        deed.ShuttleName = String.Join(" ", nameParts.SkipLast(hasSuffix ? 1 : 0));
    }

    public void OnSellMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleSellMessage args)
    {

        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        TryComp<IdCardComponent>(targetId, out var idCard);
        TryComp<ShipyardVoucherComponent>(targetId, out var voucher);
        if (idCard is null && voucher is null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed) || deed.ShuttleUid is not { Valid: true } shuttleUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        bool voucherUsed = deed.PurchasedWithVoucher;

        if (!TryComp<BankAccountComponent>(player, out var bank))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-bank"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (_station.GetOwningStation(uid) is not { Valid: true } stationUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (_station.GetOwningStation(shuttleUid) is { Valid: true } shuttleStation
            && TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage)
            && keyStorage.Key != null
            && keyStorage.Key.Value.OriginStation == shuttleStation
            && _records.TryGetRecord<GeneralStationRecord>(keyStorage.Key.Value, out var record))
        {
            //_records.RemoveRecord(keyStorage.Key.Value);
            _records.AddRecordEntry(stationUid, record);
            _records.Synchronize(stationUid);
        }

        var shuttleName = ToPrettyString(shuttleUid); // Grab the name before it gets 1984'd
        var shuttleNetEntity = _entityManager.GetNetEntity(shuttleUid); // same with the netEntity for shuttle records

        // Check for shipyard blacklisting components
        var disableSaleQuery = GetEntityQuery<ShipyardSellConditionComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();
        var disableSaleMsg = FindDisableShipyardSaleObjects(shuttleUid, (ShipyardConsoleUiKey)args.UiKey, disableSaleQuery, xformQuery);
        if (disableSaleMsg != null)
        {
            ConsolePopup(player, Loc.GetString(disableSaleMsg));
            PlayDenySound(player, uid, component);
            return;
        }

        var saleResult = TrySellShuttle(stationUid, shuttleUid, uid, out var bill);
        if (saleResult.Error != ShipyardSaleError.Success)
        {
            switch (saleResult.Error)
            {
                case ShipyardSaleError.Undocked:
                    ConsolePopup(player, Loc.GetString("shipyard-console-sale-not-docked"));
                    break;
                case ShipyardSaleError.OrganicsAboard:
                    ConsolePopup(player, Loc.GetString("shipyard-console-sale-organic-aboard", ("name", saleResult.OrganicName ?? "Somebody")));
                    break;
                case ShipyardSaleError.InvalidShip:
                    ConsolePopup(player, Loc.GetString("shipyard-console-sale-invalid-ship"));
                    break;
                default:
                    ConsolePopup(player, Loc.GetString("shipyard-console-sale-unknown-reason", ("reason", saleResult.Error.ToString())));
                    break;
            }
            PlayDenySound(player, uid, component);
            return;
        }

        // Update shuttle records
        _shuttleRecordsSystem.TrySetSaleTime(shuttleNetEntity);

        RemComp<ShuttleDeedComponent>(targetId);

        if (!voucherUsed)
        {
            if (!component.IgnoreBaseSaleRate)
                bill = (int)(bill * _baseSaleRate);

            int originalBill = bill;
            foreach (var (account, taxCoeff) in component.TaxAccounts)
            {
                var tax = CalculateSalesTax(originalBill, taxCoeff);
                _bank.TrySectorDeposit(account, tax, LedgerEntryType.BlackMarketShipyardTax);
                bill -= tax;
            }
            bill = int.Max(0, bill);

            _bank.TryBankDeposit(player, bill);
            PlayConfirmSound(player, uid, component);
        }

        var name = GetFullName(deed);
        SendSellMessage(uid, deed.ShuttleOwner!, name, component.ShipyardChannel, player, secret: false);
        if (component.SecretShipyardChannel is { } secretChannel)
            SendSellMessage(uid, deed.ShuttleOwner!, name, secretChannel, player, secret: true);

        EntityUid? refreshId = targetId;

        if (voucherUsed)
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to sell {shuttleName} (purchased with voucher) via {ToPrettyString(uid)}");
        else
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} used {ToPrettyString(targetId)} to sell {shuttleName} for {bill} credits via {ToPrettyString(uid)}");

        // No uses on the voucher left, destroy it.
        if (voucher != null
            && voucher!.RedemptionsLeft <= 0
            && voucher!.DestroyOnEmpty)
        {
            QueueDel(targetId);
            refreshId = null;
        }

        RefreshState(uid, bank.Balance, true, null, 0, refreshId, (ShipyardConsoleUiKey)args.UiKey, voucherUsed);
    }

    private void OnConsoleUIOpened(EntityUid uid, ShipyardConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (!component.Initialized)
            return;

        // kind of cursed. We need to update the UI when an Id is entered, but the UI needs to know the player characters bank account.
        if (!TryComp<ActivatableUIComponent>(uid, out var uiComp) || uiComp.Key == null)
            return;

        if (args.Actor is not { Valid: true } player)
            return;

        //      mayhaps re-enable this later for HoS/SA
        //        var station = _station.GetOwningStation(uid);

        if (!TryComp<BankAccountComponent>(player, out var bank))
            return;

        var targetId = component.TargetIdSlot.ContainerSlot?.ContainedEntity;

        if (TryComp<ShuttleDeedComponent>(targetId, out var deed))
        {
            if (Deleted(deed!.ShuttleUid))
            {
                RemComp<ShuttleDeedComponent>(targetId!.Value);
                return;
            }
        }

        var voucherUsed = HasComp<ShipyardVoucherComponent>(targetId);

        int sellValue = 0;
        if (deed?.ShuttleUid != null)
        {
            sellValue = (int)_pricing.AppraiseGrid((EntityUid)(deed?.ShuttleUid!), LacksPreserveOnSaleComp);
            sellValue = CalculateShipResaleValue((uid, component), sellValue);
        }

        var fullName = deed != null ? GetFullName(deed) : null;
        RefreshState(uid, bank.Balance, true, fullName, sellValue, targetId, (ShipyardConsoleUiKey)args.UiKey, voucherUsed);
    }

    private void ConsolePopup(EntityUid uid, string text)
    {
        _popup.PopupEntity(text, uid);
    }

    private void SendPurchaseMessage(EntityUid uid, EntityUid player, string name, string shipyardChannel, bool secret)
    {
        var channel = _prototypeManager.Index<RadioChannelPrototype>(shipyardChannel);

        if (secret)
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-docking-secret"), channel, uid);
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-docking-secret"), InGameICChatType.Speak, true);
        }
        else
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-docking", ("owner", player), ("vessel", name)), channel, uid);
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-docking", ("owner", player!), ("vessel", name)), InGameICChatType.Speak, true);
        }
    }

    private void SendSellMessage(EntityUid uid, string? player, string name, string shipyardChannel, EntityUid seller, bool secret)
    {
        var channel = _prototypeManager.Index<RadioChannelPrototype>(shipyardChannel);

        if (secret)
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-leaving-secret"), channel, uid);
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-leaving-secret"), InGameICChatType.Speak, true);
        }
        else
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-leaving", ("owner", player!), ("vessel", name!), ("player", seller)), channel, uid);
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-leaving", ("owner", player!), ("vessel", name!), ("player", seller)), InGameICChatType.Speak, true);
        }
    }

    private void PlayDenySound(EntityUid playerUid, EntityUid consoleUid, ShipyardConsoleComponent component)
    {
        if (_timing.CurTime >= component.NextDenySoundTime)
        {
            component.NextDenySoundTime = _timing.CurTime + component.DenySoundDelay;
            _audio.PlayPvs(_audio.ResolveSound(component.ErrorSound), consoleUid);
        }
    }

    private void PlayConfirmSound(EntityUid playerUid, EntityUid consoleUid, ShipyardConsoleComponent component)
    {
        _audio.PlayEntity(component.ConfirmSound, playerUid, consoleUid);
    }

    private void OnItemSlotChanged(EntityUid uid, ShipyardConsoleComponent component, ContainerModifiedMessage args)
    {
        if (!component.Initialized)
            return;

        if (args.Container.ID != component.TargetIdSlot.ID)
            return;

        // kind of cursed. We need to update the UI when an Id is entered, but the UI needs to know the player characters bank account.
        if (!TryComp<ActivatableUIComponent>(uid, out var uiComp) || uiComp.Key == null)
            return;

        var uiUsers = _ui.GetActors(uid, uiComp.Key);

        foreach (var user in uiUsers)
        {
            if (user is not { Valid: true } player)
                continue;

            if (!TryComp<BankAccountComponent>(player, out var bank))
                continue;

            var targetId = component.TargetIdSlot.ContainerSlot?.ContainedEntity;

            if (TryComp<ShuttleDeedComponent>(targetId, out var deed))
            {
                if (Deleted(deed!.ShuttleUid))
                {
                    RemComp<ShuttleDeedComponent>(targetId!.Value);
                    continue;
                }
            }

            var voucherUsed = HasComp<ShipyardVoucherComponent>(targetId);

            int sellValue = 0;
            if (deed?.ShuttleUid != null)
            {
                sellValue = (int)_pricing.AppraiseGrid(deed.ShuttleUid.Value, LacksPreserveOnSaleComp);
                sellValue = CalculateShipResaleValue((uid, component), sellValue);
            }

            var fullName = deed != null ? GetFullName(deed) : null;
            RefreshState(uid,
                bank.Balance,
                true,
                fullName,
                sellValue,
                targetId,
                (ShipyardConsoleUiKey)uiComp.Key,
                voucherUsed);

        }
    }

    /// <summary>
    /// Looks for a living, sapient being aboard a particular entity.
    /// </summary>
    /// <param name="uid">The entity to search (e.g. a shuttle, a station)</param>
    /// <param name="mobQuery">A query to get the MobState from an entity</param>
    /// <param name="xformQuery">A query to get the transform component of an entity</param>
    /// <returns>The name of the sapient being if one was found, null otherwise.</returns>
    public string? FoundOrganics(EntityUid uid, EntityQuery<MobStateComponent> mobQuery, EntityQuery<TransformComponent> xformQuery)
    {
        var xform = xformQuery.GetComponent(uid);
        var childEnumerator = xform.ChildEnumerator;

        while (childEnumerator.MoveNext(out var child))
        {
            // Ghosts don't stop a ship sale.
            if (HasComp<GhostComponent>(child))
                continue;

            // Check if we have a player entity that's either still around or alive and may come back
            if (_mind.TryGetMind(child, out _, out var mindComp)
                && (mindComp.UserId != null && _player.ValidSessionId(mindComp.UserId.Value)
                || !_mind.IsCharacterDeadPhysically(mindComp)))
            {
                return Name(child);
            }
            else
            {
                var charName = FoundOrganics(child, mobQuery, xformQuery);
                if (charName != null)
                    return charName;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks for any entities marked as preventing sale on a shuttle
    /// </summary>
    /// <param name="shuttle">The entity to search (e.g. a shuttle, a station)</param>
    /// <param name="key">The UI key of the current shipyard console. Used to see if the shipyard should ignore this check</param>
    /// <param name="disableSaleQuery">A query to get any marked objects from an entity</param>
    /// <param name="xformQuery">A query to get the transform component of an entity</param>
    /// <returns>The reason that a shuttle should be blocked from sale, null otherwise.</returns>
    public string? FindDisableShipyardSaleObjects(EntityUid shuttle, ShipyardConsoleUiKey key, EntityQuery<ShipyardSellConditionComponent> disableSaleQuery, EntityQuery<TransformComponent> xformQuery)
    {
        var xform = xformQuery.GetComponent(shuttle);
        var childEnumerator = xform.ChildEnumerator;

        while (childEnumerator.MoveNext(out var child))
        {
            if (disableSaleQuery.TryGetComponent(child, out var disableSale)
                && disableSale.BlockSale is true
                && !disableSale.AllowedShipyardTypes.Contains(key))
            {
                return disableSale.Reason ?? "shipyard-console-fallback-prevent-sale";
            }
        }

        return null;
    }

    private struct IDShipAccesses
    {
        public IReadOnlyCollection<ProtoId<AccessLevelPrototype>> Tags;
        public IReadOnlyCollection<ProtoId<AccessGroupPrototype>> Groups;
    }

    private struct VoucherShipAccesses()
    {
        public bool IsVoucher = false;
        public List<VesselClass> Classes = new();
        public List<string> Vessels = new();
    }

    /// <summary>
    ///   Returns all shuttle prototype IDs the given shipyard console can offer.
    /// </summary>
    public (List<string> available, List<string> unavailable) GetAvailableShuttles(EntityUid uid, ShipyardConsoleUiKey? key = null,
        ShipyardListingComponent? listing = null, EntityUid? targetId = null)
    {
        var available = new List<string>();
        var unavailable = new List<string>();

        if (key == null && TryComp<UserInterfaceComponent>(uid, out var ui))
        {
            // Try to find a ui key that is an instance of the shipyard console ui key
            foreach (var (k, v) in ui.Actors)
            {
                if (k is ShipyardConsoleUiKey shipyardKey)
                {
                    key = shipyardKey;
                    break;
                }
            }
        }

        // No listing provided, try to get the current one from the console being used as a default.
        if (listing is null)
            TryComp(uid, out listing);

        // Construct access set from input type (voucher or ID card)
        IDShipAccesses accesses;
        VoucherShipAccesses voucherAccesses = new();
        bool initialHasAccess = true;
        if (TryComp<ShipyardVoucherComponent>(targetId, out var voucher))
        {
            voucherAccesses.IsVoucher = true;
            if (voucher.ConsoleType == key)
            {
                accesses.Tags = voucher.Access;
                accesses.Groups = voucher.AccessGroups;
            }
            else
            {
                accesses.Tags = new HashSet<ProtoId<AccessLevelPrototype>>();
                accesses.Groups = new HashSet<ProtoId<AccessGroupPrototype>>();
                initialHasAccess = false;
            }
            voucherAccesses.Classes = voucher.Classes.ToList();
            voucherAccesses.Vessels = voucher.Vessels.ToList();
        }
        else if (TryComp<AccessComponent>(targetId, out var accessComponent))
        {
            accesses.Tags = accessComponent.Tags;
            accesses.Groups = accessComponent.Groups;
        }
        else
        {
            accesses.Tags = new HashSet<ProtoId<AccessLevelPrototype>>();
            accesses.Groups = new HashSet<ProtoId<AccessGroupPrototype>>();
        }

        foreach (var vessel in _prototypeManager.EnumeratePrototypes<VesselPrototype>())
        {
            bool hasAccess = initialHasAccess;
            // If the vessel needs access to be bought, check the user's access.
            if (!string.IsNullOrEmpty(vessel.Access))
            {
                hasAccess = false;
                // Check tags
                if (accesses.Tags.Contains(vessel.Access))
                    hasAccess = true;

                // Check each group if we haven't found access already.
                if (!hasAccess)
                {
                    foreach (var groupId in accesses.Groups)
                    {
                        var groupProto = _prototypeManager.Index(groupId);
                        if (groupProto?.Tags.Contains(vessel.Access) ?? false)
                        {
                            hasAccess = true;
                            break;
                        }
                    }
                }

            }

            // If the vessel is a voucher, check if the voucher allows it.
            if (voucherAccesses.IsVoucher)
            {
                // If the voucher has classes, check if the vessel class is allowed.
                foreach (var vesselClass in vessel.Classes)
                {
                    if (voucherAccesses.Classes.Contains(vesselClass))
                    {
                        hasAccess = true;
                        break;
                    }
                }

                // If the voucher has vessels, check if the vessel is allowed.
                if (!hasAccess && voucherAccesses.Vessels.Contains(vessel.ID))
                {
                    hasAccess = true;
                }
            }

            // Check that the listing contains the shuttle or that the shuttle is in the group that the console is looking for
            if (listing?.Shuttles.Contains(vessel.ID) ?? false ||
                key != null && key != ShipyardConsoleUiKey.Custom &&
                vessel.Group == key)
            {
                if (hasAccess)
                    available.Add(vessel.ID);
                else
                    unavailable.Add(vessel.ID);
            }
        }

        return (available, unavailable);
    }

    private void RefreshState(EntityUid uid, int balance, bool access, string? shipDeed, int shipSellValue, EntityUid? targetId, ShipyardConsoleUiKey uiKey, bool freeListings)
    {
        var newState = new ShipyardConsoleInterfaceState(
            balance,
            access,
            shipDeed,
            shipSellValue,
            targetId.HasValue,
            ((byte)uiKey),
            GetAvailableShuttles(uid, uiKey, targetId: targetId),
            uiKey.ToString(),
            freeListings,
            CalculateSellRate(uid));

        _ui.SetUiState(uid, uiKey, newState);
    }

    #region Deed Assignment
    void AssignShuttleDeedProperties(Entity<ShuttleDeedComponent> deed, EntityUid? shuttleUid, string? shuttleName, string? shuttleOwner, bool purchasedWithVoucher)
    {
        deed.Comp.ShuttleUid = shuttleUid;
        TryParseShuttleName(deed.Comp, shuttleName!);
        deed.Comp.ShuttleOwner = shuttleOwner;
        deed.Comp.PurchasedWithVoucher = purchasedWithVoucher;
        Dirty(deed);
    }

    private void OnInitDeedSpawner(EntityUid uid, StationDeedSpawnerComponent component, MapInitEvent args)
    {
        if (!HasComp<IdCardComponent>(uid)) // Test if the deed on an ID
            return;

        var xform = Transform(uid); // Get the grid the card is on
        if (xform.GridUid == null)
            return;

        if (!TryComp<ShuttleDeedComponent>(xform.GridUid.Value, out var shuttleDeed) || !TryComp<ShuttleComponent>(xform.GridUid.Value, out var shuttle) || !HasComp<TransformComponent>(xform.GridUid.Value) || shuttle == null || ShipyardMap == null)
            return;

        var output = DeedRegex.Replace($"{shuttleDeed.ShuttleOwner}", ""); // Removes content inside parentheses along with parentheses and a preceding space
        _idSystem.TryChangeFullName(uid, output); // Update the card with owner name

        var deedID = EnsureComp<ShuttleDeedComponent>(uid);
        AssignShuttleDeedProperties((uid, deedID), shuttleDeed.ShuttleUid, shuttleDeed.ShuttleName, shuttleDeed.ShuttleOwner, shuttleDeed.PurchasedWithVoucher);
    }
    #endregion

    #region Ship Pricing
    // Calculates the sell rate of a given shipyard console
    private float CalculateSellRate(Entity<ShipyardConsoleComponent?> console)
    {
        if (!Resolve(console, ref console.Comp))
            return 0.0f;

        var taxRate = 0.0f;
        foreach (var taxAccount in console.Comp.TaxAccounts)
        {
            taxRate += taxAccount.Value;
        }
        taxRate = 1.0f - taxRate;  // Return the value minus the taxes

        if (console.Comp.IgnoreBaseSaleRate)
            return taxRate;
        else
            return _baseSaleRate * taxRate;
    }

    private int CalculateShipResaleValue(Entity<ShipyardConsoleComponent?> console, int baseAppraisal)
    {
        if (!Resolve(console, ref console.Comp))
            return 0;

        int resaleValue = baseAppraisal;
        if (!console.Comp.IgnoreBaseSaleRate)
            resaleValue = (int)(_baseSaleRate * resaleValue);

        resaleValue -= CalculateTotalSalesTax(console.Comp, resaleValue);
        return resaleValue;
    }

    // Calculates total sales tax over all accounts.
    private int CalculateTotalSalesTax(ShipyardConsoleComponent component, int sellValue)
    {
        int salesTax = 0;
        foreach (var (account, taxCoeff) in component.TaxAccounts)
            salesTax += CalculateSalesTax(sellValue, taxCoeff);
        return salesTax;
    }

    // Calculates sales tax for a particular account.
    private int CalculateSalesTax(int sellValue, float taxRate)
    {
        if (float.IsFinite(taxRate) && taxRate > 0f)
            return (int)(sellValue * taxRate);
        return 0;
    }
    #endregion Ship Pricing
}
