using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Excel.Sheets;
using PandorasBox.FeaturesSetup;
using PandorasBox.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace PandorasBox.Features.Targets
{
    public unsafe class AutoInteractGathering : Feature
    {

        public override string Name => "Auto-interact with Gathering Nodes";
        public override string Description => "Interacts with gathering nodes when close enough and on the correct job.";

        public override FeatureType FeatureType => FeatureType.Targeting;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Set delay (seconds)", FloatMin = 0.1f, FloatMax = 10f, EditorSize = 300, EnforcedLimit = true)]
            public float Throttle = 0.1f;

            [FeatureConfigOption("Cooldown after gathering (seconds)", FloatMin = 0.1f, FloatMax = 10f, EditorSize = 300, EnforcedLimit = true)]
            public float Cooldown = 0.1f;

            [FeatureConfigOption("Exclude Timed Unspoiled Nodes", "", 1)]
            public bool ExcludeTimedUnspoiled = false;

            [FeatureConfigOption("Exclude Timed Ephemeral Nodes", "", 2)]
            public bool ExcludeTimedEphermeral = false;

            [FeatureConfigOption("Exclude Timed Legendary Nodes", "", 3)]
            public bool ExcludeTimedLegendary = false;

            [FeatureConfigOption("Required GP to Interact (>=)", IntMin = 0, IntMax = 1000, EditorSize = 300)]
            public int RequiredGP = 0;

            [FeatureConfigOption("Exclude Island Nodes", "", 7)]
            public bool ExcludeIsland = false;

            [FeatureConfigOption("Exclude Miner Nodes", "", 4)]
            public bool ExcludeMiner = false;

            [FeatureConfigOption("Exclude Botanist Nodes", "", 5)]
            public bool ExcludeBotanist = false;

            [FeatureConfigOption("Exclude Spearfishing Nodes", "", 6)]
            public bool ExcludeFishing = false;

            [FeatureConfigOption("Zone Whitelist", "TerritorySelection", 7)]
            public List<uint> ZoneWhitelist = [];
        }

        public Configs Config { get; private set; }

        // Cached Excel sheet lookups: built once on Enable() instead of scanning every frame
        private Dictionary<uint, GatheringPoint> _gatheringPoints;
        private Dictionary<uint, GatheringPointTransient> _gatheringTransients;
        private string _landingMessageText;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();

            _gatheringPoints = Svc.Data.GetExcelSheet<GatheringPoint>()
                .ToDictionary(x => x.RowId);
            _gatheringTransients = Svc.Data.GetExcelSheet<GatheringPointTransient>()
                .ToDictionary(x => x.RowId);

            // Cache the landing error message text so CheckIfLanding doesn't scan the sheet on every toast
            _landingMessageText = Svc.Data.GetExcelSheet<LogMessage>()
                .FirstOrDefault(x => x.RowId == 7777).Text.ExtractText();

            Svc.Framework.Update += RunFeature;
            Svc.Condition.ConditionChange += TriggerCooldown;
            Svc.Toasts.ErrorToast += CheckIfLanding;
            base.Enable();
        }

        private void CheckIfLanding(ref SeString message, ref bool isHandled)
        {
            if (message.GetText() == _landingMessageText)
            {
                TaskManager.Abort();
                TaskManager.EnqueueDelay(2000);
            }
        }

        private void TriggerCooldown(ConditionFlag flag, bool value)
        {
            if (flag == ConditionFlag.Gathering && !value)
                TaskManager.EnqueueDelay((int)(Config.Cooldown * 1000));
        }

        private void RunFeature(IFramework framework)
        {
            if (Svc.Condition[ConditionFlag.Gathering] || Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
                return;

            if (Svc.Objects.LocalPlayer is null) return;
            if (Svc.Objects.LocalPlayer.IsCasting) return;
            if (Svc.Condition[ConditionFlag.Jumping]) return;
            if (Config.ZoneWhitelist.Count > 0 && !Config.ZoneWhitelist.Contains(Player.Territory.RowId)) return;

            var nearbyNodes = Svc.Objects.Where(x => (x.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint || x.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.CardStand) && Vector3.Distance(x.Position, Player.Object.Position) < 4 && GameObjectHelper.GetHeightDifference(x) <= 4 && x.IsTargetable).ToList();
            if (nearbyNodes.Count == 0)
                return;

            var nearestNode = nearbyNodes.OrderBy(GameObjectHelper.GetTargetDistance).First();
            var baseObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)nearestNode.Address;

            if (!nearestNode.IsTargetable)
                return;

            if (Config.ExcludeIsland && MJIManager.Instance()->IsPlayerInSanctuary)
                return;

            if (nearestNode.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.CardStand && MJIManager.Instance()->IsPlayerInSanctuary && MJIManager.Instance()->CurrentMode == 1)
            {
                if (!TaskManager.IsBusy)
                {
                    TaskManager.EnqueueDelay((int)(Config.Throttle * 1000));
                    TaskManager.EnqueueWithTimeout(() => { TargetSystem.Instance()->OpenObjectInteraction(baseObj); return true; }, 1000);
                }
                return;
            }

            // Dictionary lookup: O(1) instead of a full sheet scan every frame
            if (!_gatheringPoints.TryGetValue(nearestNode.BaseId, out var gatheringPoint))
                return;

            var job = gatheringPoint.GatheringPointBase.Value.GatheringType.Value.RowId;
            var targetGp = Math.Min(Config.RequiredGP, Svc.Objects.LocalPlayer.MaxGp);

            string Folklore = "";
            if (gatheringPoint.GatheringSubCategory.IsValid && !gatheringPoint.GatheringSubCategory.Value.FolkloreBook.IsEmpty)
                Folklore = gatheringPoint.GatheringSubCategory.Value.FolkloreBook.ToString();

            // Single transient lookup: replaces three separate full-sheet .Any() scans
            if (_gatheringTransients.TryGetValue(nearestNode.BaseId, out var transient))
            {
                if (Config.ExcludeTimedUnspoiled && transient.GatheringRarePopTimeTable.Value.RowId > 0 && gatheringPoint.GatheringSubCategory.Value.Item.RowId == 0)
                    return;
                if (Config.ExcludeTimedEphermeral && transient.EphemeralStartTime != 65535)
                    return;
                if (Config.ExcludeTimedLegendary && transient.GatheringRarePopTimeTable.Value.RowId > 0 && Folklore.Length > 0 && gatheringPoint.GatheringSubCategory.Value.Item.RowId != 0)
                    return;
            }

            if (!Config.ExcludeMiner && job is 0 or 1 && Svc.Objects.LocalPlayer.ClassJob.RowId == 16 && Svc.Objects.LocalPlayer.CurrentGp >= targetGp && !TaskManager.IsBusy)
            {
                TaskManager.EnqueueDelay((int)(Config.Throttle * 1000));
                TaskManager.Enqueue(() => { Chat.SendMessage("/automove off"); });
                TaskManager.EnqueueWithTimeout(() => { TargetSystem.Instance()->OpenObjectInteraction(baseObj); return true; }, 1000);
                return;
            }
            if (!Config.ExcludeBotanist && job is 2 or 3 && Svc.Objects.LocalPlayer.ClassJob.RowId == 17 && Svc.Objects.LocalPlayer.CurrentGp >= targetGp && !TaskManager.IsBusy)
            {
                TaskManager.EnqueueDelay((int)(Config.Throttle * 1000));
                TaskManager.Enqueue(() => { Chat.SendMessage("/automove off"); });
                TaskManager.EnqueueWithTimeout(() => { TargetSystem.Instance()->OpenObjectInteraction(baseObj); return true; }, 1000);
                return;
            }
            if (!Config.ExcludeFishing && job is 4 or 5 && Svc.Objects.LocalPlayer.ClassJob.RowId == 18 && Svc.Objects.LocalPlayer.CurrentGp >= targetGp && !TaskManager.IsBusy)
            {
                TaskManager.EnqueueDelay((int)(Config.Throttle * 1000));
                TaskManager.Enqueue(() => { Chat.SendMessage("/automove off"); });
                TaskManager.EnqueueWithTimeout(() => { TargetSystem.Instance()->OpenObjectInteraction(baseObj); return true; }, 1000);
                return;
            }
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= RunFeature;
            base.Disable();
        }
    }
}
