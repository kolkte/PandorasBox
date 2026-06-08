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
        // Throttle: only run expensive checks every 50ms (0.05 seconds)
        private DateTime lastCheck = DateTime.MinValue;
        private const double ThrottleIntervalMs = 50.0;

        // Cached data for gathering points
        private Dictionary<uint, uint> gatheringJobCache = new();          // BaseId -> job (0/1 Miner, 2/3 Botanist, 4/5 Fisher)
        private Dictionary<uint, bool> isTimedUnspoiledCache = new();
        private Dictionary<uint, bool> isTimedEphemeralCache = new();
        private Dictionary<uint, (bool IsLegendary, string Folklore)> legendaryInfoCache = new(); // BaseId -> (IsLegendary, FolkloreBook)

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

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            // Pre-cache all gathering point data on enable (one-time)
            CacheGatheringPointData();
            Svc.Framework.Update += RunFeature;
            Svc.Condition.ConditionChange += TriggerCooldown;
            Svc.Toasts.ErrorToast += CheckIfLanding;
            base.Enable();
        }

        private void CacheGatheringPointData()
        {
            var gatheringPointSheet = Svc.Data.GetExcelSheet<GatheringPoint>();
            var gatheringPointTransientSheet = Svc.Data.GetExcelSheet<GatheringPointTransient>();
            var gatheringSubCategorySheet = Svc.Data.GetExcelSheet<GatheringSubCategory>();
            var gatheringTypeSheet = Svc.Data.GetExcelSheet<GatheringType>();

            foreach (var point in gatheringPointSheet)
            {
                uint baseId = point.RowId;
                // Job from GatheringType
                var gatheringType = point.GatheringPointBase.Value?.GatheringType.Value;
                if (gatheringType != null)
                {
                    gatheringJobCache[baseId] = gatheringType.RowId;
                }

                // Timed Unspoiled: rare pop time table exists and subcategory item is 0
                var transient = gatheringPointTransientSheet.FirstOrDefault(t => t.RowId == baseId);
                if (transient.RowId != 0 && transient.GatheringRarePopTimeTable.Value.RowId > 0)
                {
                    var subcategory = point.GatheringSubCategory.Value;
                    if (subcategory.RowId != 0 && subcategory.Item.RowId == 0)
                        isTimedUnspoiledCache[baseId] = true;
                }

                // Timed Ephemeral: EphemeralStartTime != 65535
                if (transient.RowId != 0 && transient.EphemeralStartTime != 65535)
                    isTimedEphemeralCache[baseId] = true;

                // Legendary: rare pop time table + folklore book + subcategory item != 0
                if (transient.RowId != 0 && transient.GatheringRarePopTimeTable.Value.RowId > 0)
                {
                    var subcategory = point.GatheringSubCategory.Value;
                    if (subcategory.RowId != 0 && subcategory.Item.RowId != 0)
                    {
                        string folklore = subcategory.FolkloreBook.IsEmpty ? "" : subcategory.FolkloreBook.ToString();
                        legendaryInfoCache[baseId] = (true, folklore);
                    }
                }
            }
        }

        private void CheckIfLanding(ref SeString message, ref bool isHandled)
        {
            if (message.GetText() == Svc.Data.GetExcelSheet<LogMessage>().First(x => x.RowId == 7777).Text.ExtractText())
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
            // Throttle: only run every ThrottleIntervalMs milliseconds
            if ((DateTime.Now - lastCheck).TotalMilliseconds < ThrottleIntervalMs)
                return;
            lastCheck = DateTime.Now;

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
            {
                return;
            }

            if (nearestNode.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.CardStand && MJIManager.Instance()->IsPlayerInSanctuary && MJIManager.Instance()->CurrentMode == 1)
            {
                if (!TaskManager.IsBusy)
                {
                    TaskManager.EnqueueDelay((int)(Config.Throttle * 1000));
                    TaskManager.EnqueueWithTimeout(() => { TargetSystem.Instance()->OpenObjectInteraction(baseObj); return true; }, 1000);
                }
                return;
            }

            uint baseId = nearestNode.BaseId;

            // Use cached job
            if (!gatheringJobCache.TryGetValue(baseId, out uint job))
                return;

            int targetGp = Math.Min(Config.RequiredGP, Svc.Objects.LocalPlayer.MaxGp);

            // Exclude checks using cached data
            if (Config.ExcludeTimedUnspoiled && isTimedUnspoiledCache.ContainsKey(baseId))
                return;
            if (Config.ExcludeTimedEphermeral && isTimedEphemeralCache.ContainsKey(baseId))
                return;
            if (Config.ExcludeTimedLegendary && legendaryInfoCache.TryGetValue(baseId, out var legendary) && legendary.IsLegendary)
                return;

            if (!Config.ExcludeMiner && (job is 0 or 1) && Svc.Objects.LocalPlayer.ClassJob.RowId == 16 && Svc.Objects.LocalPlayer.CurrentGp >= targetGp && !TaskManager.IsBusy)
            {
                TaskManager.EnqueueDelay((int)(Config.Throttle * 1000));
                TaskManager.Enqueue(() => { Chat.SendMessage("/automove off"); });
                TaskManager.EnqueueWithTimeout(() => { TargetSystem.Instance()->OpenObjectInteraction(baseObj); return true; }, 1000);
                return;
            }
            if (!Config.ExcludeBotanist && (job is 2 or 3) && Svc.Objects.LocalPlayer.ClassJob.RowId == 17 && Svc.Objects.LocalPlayer.CurrentGp >= targetGp && !TaskManager.IsBusy)
            {
                TaskManager.EnqueueDelay((int)(Config.Throttle * 1000));
                TaskManager.Enqueue(() => { Chat.SendMessage("/automove off"); });
                TaskManager.EnqueueWithTimeout(() => { TargetSystem.Instance()->OpenObjectInteraction(baseObj); return true; }, 1000);
                return;
            }
            if (!Config.ExcludeFishing && (job is 4 or 5) && Svc.Objects.LocalPlayer.ClassJob.RowId == 18 && Svc.Objects.LocalPlayer.CurrentGp >= targetGp && !TaskManager.IsBusy)
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
