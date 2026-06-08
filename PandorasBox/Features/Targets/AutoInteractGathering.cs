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
        // --- Caching fields added ---
        private Dictionary<uint, uint> cachedJob = new();
        private Dictionary<uint, (uint SubCatId, string Folklore)> cachedSubCat = new();
        private Dictionary<uint, (bool HasRarePop, bool IsEphemeral, bool IsUnspoiled, bool IsLegendary)> cachedTransient = new();
        // ----------------------------

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
            // --- Build caches once ---
            CacheGatheringData();
            // -------------------------
            Svc.Framework.Update += RunFeature;
            Svc.Condition.ConditionChange += TriggerCooldown;
            Svc.Toasts.ErrorToast += CheckIfLanding;
            base.Enable();
        }

        // --- New caching method ---
        private void CacheGatheringData()
        {
            var gpSheet = Svc.Data.GetExcelSheet<GatheringPoint>();
            var transSheet = Svc.Data.GetExcelSheet<GatheringPointTransient>();
            foreach (var point in gpSheet)
            {
                uint id = point.RowId;
                // job
                if (point.GatheringPointBase.IsValid)
                {
                    var gt = point.GatheringPointBase.Value.GatheringType.Value;
                    if (gt.RowId != 0) cachedJob[id] = gt.RowId;
                }
                // subcategory & folklore
                if (point.GatheringSubCategory.IsValid)
                {
                    var sub = point.GatheringSubCategory.Value;
                    string folk = sub.FolkloreBook.IsEmpty ? "" : sub.FolkloreBook.ToString();
                    cachedSubCat[id] = (sub.RowId, folk);
                }
                else
                {
                    cachedSubCat[id] = (0, "");
                }
                // transient
                var trans = transSheet.FirstOrDefault(t => t.RowId == id);
                if (trans.RowId != 0)
                {
                    bool hasRare = trans.GatheringRarePopTimeTable.Value.RowId > 0;
                    bool ephemeral = trans.EphemeralStartTime != 65535;
                    bool unspoiled = hasRare && cachedSubCat.TryGetValue(id, out var sc) && sc.SubCatId != 0 && sc.Folklore == "";
                    bool legendary = hasRare && cachedSubCat.TryGetValue(id, out var sc2) && sc2.SubCatId != 0 && sc2.Folklore != "";
                    cachedTransient[id] = (hasRare, ephemeral, unspoiled, legendary);
                }
                else
                {
                    cachedTransient[id] = (false, false, false, false);
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

            // --- Use cached job, fallback to Excel if missing ---
            if (!cachedJob.TryGetValue(baseId, out uint job))
            {
                var gp = Svc.Data.GetExcelSheet<GatheringPoint>().First(x => x.RowId == nearestNode.BaseId);
                job = gp.GatheringPointBase.Value.GatheringType.Value.RowId;
            }

            var targetGp = Math.Min(Config.RequiredGP, Svc.Objects.LocalPlayer.MaxGp);

            // --- Use cached folklore, fallback ---
            string Folklore = "";
            if (cachedSubCat.TryGetValue(baseId, out var subCatData))
                Folklore = subCatData.Folklore;
            else
            {
                var gp = Svc.Data.GetExcelSheet<GatheringPoint>().First(x => x.RowId == nearestNode.BaseId);
                if (gp.GatheringSubCategory.IsValid && !gp.GatheringSubCategory.Value.FolkloreBook.IsEmpty)
                    Folklore = gp.GatheringSubCategory.Value.FolkloreBook.ToString();
            }

            // --- Use cached transient info, fallback ---
            if (!cachedTransient.TryGetValue(baseId, out var trans))
            {
                var gp = Svc.Data.GetExcelSheet<GatheringPoint>().First(x => x.RowId == nearestNode.BaseId);
                var tr = Svc.Data.GetExcelSheet<GatheringPointTransient>().FirstOrDefault(x => x.RowId == nearestNode.BaseId);
                bool hasRare = tr.RowId != 0 && tr.GatheringRarePopTimeTable.Value.RowId > 0;
                bool ephemeral = tr.RowId != 0 && tr.EphemeralStartTime != 65535;
                bool unspoiled = hasRare && gp.GatheringSubCategory.IsValid && gp.GatheringSubCategory.Value.Item.RowId == 0;
                bool legendary = hasRare && gp.GatheringSubCategory.IsValid && gp.GatheringSubCategory.Value.Item.RowId != 0 && Folklore.Length > 0;
                trans = (hasRare, ephemeral, unspoiled, legendary);
            }

            if (Config.ExcludeTimedUnspoiled && trans.IsUnspoiled)
                return;
            if (Config.ExcludeTimedEphermeral && trans.IsEphemeral)
                return;
            if (Config.ExcludeTimedLegendary && trans.IsLegendary && Folklore.Length > 0)
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
