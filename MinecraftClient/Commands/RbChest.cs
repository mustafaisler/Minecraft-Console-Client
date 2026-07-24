using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Brigadier.NET;
using Brigadier.NET.Builder;
using MinecraftClient.CommandHandler;
using MinecraftClient.Inventory;

namespace MinecraftClient.Commands
{
    /// <summary>
    /// RakitBot'a ozel dogrulamali sandik transferi.
    /// Kullanim (yalniz RakitBot backend'i kurar, musteriye ham acilmaz):
    ///   /rbchest deposit  <ItemType> <count>   — oyuncudan acik container'a
    ///   /rbchest withdraw <ItemType> <count>   — acik container'dan oyuncuya
    /// On kosul: bir container ACIK olmali (backend once /useblock x y z gonderir).
    /// Sonuc konsola TEK kanonik satir olarak yazilir (RakitBot bunu ayiklar):
    ///   [RbChest] OK action=deposit item=Diamond istenen=5 tasinan=5 kaynak=12->7 hedef=0->5
    ///   [RbChest] FAIL sebep=...
    /// Guvenlik: tek is kilidi (esdegerli ikinci cagri reddedilir), 20 sn toplam
    /// timeout, her pencere aksiyonu sonrasi dogrulama beklemesi, bitiste container
    /// KAPATILIR (basari/hata farketmez).
    /// </summary>
    class RbChest : Command
    {
        public override string CmdName { get { return "rbchest"; } }
        public override string CmdUsage { get { return "/rbchest <deposit|withdraw> <ItemType> <count>"; } }
        public override string CmdDesc { get { return "RakitBot dogrulamali sandik transferi"; } }

        private static int busy = 0; // Interlocked kilit: 0 = bos, 1 = calisiyor
        private const int TOTAL_TIMEOUT_MS = 20000;
        private const int ACTION_SETTLE_MS = 250;
        private const int MAX_COUNT = 512;

        public override void RegisterCommand(CommandDispatcher<CmdResult> dispatcher)
        {
            dispatcher.Register(l => l.Literal(CmdName)
                .Then(l => l.Literal("deposit")
                    .Then(l => l.Argument("ItemType", MccArguments.ItemType())
                        .Then(l => l.Argument("Count", Arguments.Integer(min: 1, max: MAX_COUNT))
                            .Executes(r => Start(r.Source, true, MccArguments.GetItemType(r, "ItemType"), Arguments.GetInteger(r, "Count"))))))
                .Then(l => l.Literal("withdraw")
                    .Then(l => l.Argument("ItemType", MccArguments.ItemType())
                        .Then(l => l.Argument("Count", Arguments.Integer(min: 1, max: MAX_COUNT))
                            .Executes(r => Start(r.Source, false, MccArguments.GetItemType(r, "ItemType"), Arguments.GetInteger(r, "Count"))))))
            );
        }

        private int Start(CmdResult r, bool deposit, ItemType itemType, int count)
        {
            McClient handler = CmdResult.currentHandler!;

            if (!handler.GetInventoryEnabled())
                return r.SetAndReturn(CmdResult.Status.FailNeedInventory);

            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            {
                handler.Log.Info("[RbChest] FAIL sebep=baska_transfer_calisiyor");
                return r.SetAndReturn(CmdResult.Status.Fail);
            }

            // Acik container bul (id > 0 en buyuk pencere).
            var ids = handler.GetInventories().Keys.ToList();
            int containerId = ids.Count > 0 ? ids.Max() : 0;
            if (containerId <= 0)
            {
                Interlocked.Exchange(ref busy, 0);
                handler.Log.Info("[RbChest] FAIL sebep=acik_container_yok");
                return r.SetAndReturn(CmdResult.Status.Fail);
            }

            new Thread(() => Worker(handler, containerId, deposit, itemType, count)).Start();
            return r.SetAndReturn(CmdResult.Status.Done);
        }

        private static void Worker(McClient handler, int containerId, bool deposit, ItemType itemType, int count)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int srcBefore = 0, dstBefore = 0;
            try
            {
                Container? container = handler.GetInventory(containerId);
                if (container is null) { Fail(handler, "container_kayboldu"); return; }

                int containerSlots = ContainerSlotCount(container);

                srcBefore = CountIn(handler, containerId, containerSlots, itemType, srcIsContainer: !deposit);
                dstBefore = CountIn(handler, containerId, containerSlots, itemType, srcIsContainer: deposit);

                if (srcBefore <= 0) { Fail(handler, "kaynakta_esya_yok"); return; }
                int target = Math.Min(count, srcBefore);

                int moved = 0;
                while (moved < target && sw.ElapsedMilliseconds < TOTAL_TIMEOUT_MS)
                {
                    container = handler.GetInventory(containerId);
                    if (container is null) { Fail(handler, "container_kapandi"); return; }

                    // Kaynak bolgede esyayi tut: deposit -> oyuncu bolgesi,
                    // withdraw -> container bolgesi.
                    var slotEntry = container.Items
                        .Where(kv => kv.Value.Type == itemType &&
                                     (deposit ? kv.Key >= containerSlots : kv.Key < containerSlots))
                        .OrderBy(kv => kv.Key)
                        .Cast<KeyValuePair<int, Item>?>()
                        .FirstOrDefault();
                    if (slotEntry is null) break; // kaynak tukendi

                    int slot = slotEntry.Value.Key;
                    int stack = slotEntry.Value.Value.Count;
                    int need = target - moved;

                    if (stack <= need)
                    {
                        // Tum stack'i tasi: shift-click yon secimini kendisi yapar.
                        handler.DoWindowAction(containerId, slot, WindowActionType.ShiftClick);
                        Thread.Sleep(ACTION_SETTLE_MS);
                    }
                    else
                    {
                        // Kismi tasima: stack'i eline al, hedef bolgedeki bos/ayni-tur
                        // slota "need" adet tek-tek birak, kalani geri koy.
                        container = handler.GetInventory(containerId);
                        if (container is null) { Fail(handler, "container_kapandi"); return; }
                        int emptyTarget = FindTargetSlot(container, containerSlots, itemType, targetIsContainer: deposit, need);
                        if (emptyTarget < 0) { Fail(handler, "hedefte_yer_yok"); return; } // OK satiri basilmaz (cift mesaj bug fix)

                        handler.DoWindowAction(containerId, slot, WindowActionType.LeftClick); // eline al
                        Thread.Sleep(ACTION_SETTLE_MS);
                        for (int i = 0; i < need && sw.ElapsedMilliseconds < TOTAL_TIMEOUT_MS; i++)
                        {
                            handler.DoWindowAction(containerId, emptyTarget, WindowActionType.RightClick); // 1 adet birak
                            Thread.Sleep(80);
                        }
                        handler.DoWindowAction(containerId, slot, WindowActionType.LeftClick); // kalani geri koy
                        Thread.Sleep(ACTION_SETTLE_MS);
                    }

                    int srcNow = CountIn(handler, containerId, containerSlots, itemType, srcIsContainer: !deposit);
                    moved = srcBefore - srcNow;
                    if (moved < 0) moved = 0;
                }

                int srcAfter = CountIn(handler, containerId, containerSlots, itemType, srcIsContainer: !deposit);
                int dstAfter = CountIn(handler, containerId, containerSlots, itemType, srcIsContainer: deposit);
                int gercekTasinan = srcBefore - srcAfter;

                handler.Log.Info(string.Format(
                    "[RbChest] OK action={0} item={1} istenen={2} tasinan={3} kaynak={4}->{5} hedef={6}->{7}",
                    deposit ? "deposit" : "withdraw", itemType, count, gercekTasinan,
                    srcBefore, srcAfter, dstBefore, dstAfter));
            }
            catch (Exception e)
            {
                Fail(handler, "istisna: " + e.Message);
            }
            finally
            {
                try { handler.CloseInventory(containerId); } catch { }
                Interlocked.Exchange(ref busy, 0);
            }
        }

        private static void Fail(McClient handler, string reason)
        {
            handler.Log.Info("[RbChest] FAIL sebep=" + reason);
        }

        /// <summary>Container penceresinde container'a ait slot sayisi
        /// (kalan slotlar oyuncu envanteri + hotbar'dir).</summary>
        private static int ContainerSlotCount(Container container)
        {
            // Pencere toplam slotlari - oyuncu bolgesi (27 envanter + 9 hotbar).
            // Chest/DoubleChest/Barrel vb. icin guvenli genel kural.
            int total = container.Type.SlotCount();
            int playerArea = 36;
            int n = total - playerArea;
            return n > 0 ? n : 27;
        }

        private static int CountIn(McClient handler, int containerId, int containerSlots, ItemType itemType, bool srcIsContainer)
        {
            Container? c = handler.GetInventory(containerId);
            if (c is null) return 0;
            return c.Items
                .Where(kv => kv.Value.Type == itemType &&
                             (srcIsContainer ? kv.Key < containerSlots : kv.Key >= containerSlots))
                .Sum(kv => kv.Value.Count);
        }

        /// <summary>Hedef bolgede ayni turden yarim stack veya bos slot bulur.</summary>
        private static int FindTargetSlot(Container container, int containerSlots, ItemType itemType, bool targetIsContainer, int need)
        {
            Func<int, bool> inTarget = (slot) => targetIsContainer ? slot < containerSlots : slot >= containerSlots;

            // Ayni tur, uzerine "need" sigacak yarim stack.
            foreach (var kv in container.Items.OrderBy(kv => kv.Key))
            {
                if (!inTarget(kv.Key)) continue;
                if (kv.Value.Type == itemType && kv.Value.Count + need <= 64) return kv.Key;
            }
            // Bos slot.
            int limitStart = targetIsContainer ? 0 : containerSlots;
            int limitEnd = targetIsContainer ? containerSlots : containerSlots + 36;
            for (int i = limitStart; i < limitEnd; i++)
                if (!container.Items.ContainsKey(i)) return i;
            return -1;
        }
    }
}
