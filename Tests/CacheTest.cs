using System;
using System.IO;
using System.Text;

// 檢查「順暢切換」的兩個純邏輯閘門（切角色不再冷載）：
// 1) PmxCachePolicy：refcount 歸零不再硬逐出——entry 保留為 idle（上限 PmxCachePolicy.IdleCap，
//    超限才逐出最舊 idle），再 Acquire 從 idle 回到 active。選單切角色、跨戰局重掛、
//    pooled-body re-init 因此都走 warm（不重冷解析、不重讀貼圖）。
// 2) PmxContainer.ReadName：只讀 magic+版本+顯示名稱——warm 命中時不需要把整個容器
//    （數十 MB 的 PMX+貼圖）從磁碟讀進來。
namespace GF2.Tests
{
    public static class CacheTest
    {
        private static int _fail;

        public static int Run()
        {
            RefcountRetention();
            PingPongStaysWarm();
            CapEvictsOldestIdle();
            ActiveEntryNeverEvicted();
            UntrackedReleaseIsNoOp();
            CapOverrideKeepsMoreIdle();
            RemovePurgesDeadEntry();
            ReadNameSkipsPayload();
            ReadNameRejectsBadMagic();

            if (_fail > 0)
            {
                Console.Error.WriteLine($"cache: {_fail} checks failed");
                return 1;
            }
            Console.WriteLine("cache: OK");
            return 0;
        }

        private static void Check(bool cond, string what)
        {
            if (cond) return;
            _fail++;
            Console.Error.WriteLine($"cache: {what} FAILED");
        }

        // refcount 語義：>1 降 1 不逐出；歸 0 保留為 idle；再 Acquire 回 active。
        private static void RefcountRetention()
        {
            var p = new PmxCachePolicy();
            p.Acquire("A");
            p.Acquire("A");
            var v = p.Release("A");
            Check(v.Count == 0 && p.RefCount("A") == 1, "two acquires then one release → refs 1, no evict");
            v = p.Release("A");
            Check(v.Count == 0, "release to zero retains the entry (no evict victim)");
            Check(p.RefCount("A") == 0 && p.IdleCount == 1, "released entry becomes retained idle");
            p.Acquire("A");
            Check(p.RefCount("A") == 1 && p.IdleCount == 0, "acquire lifts a retained idle entry back to active");
        }

        // 選單實測場景：A↔B 來回切——兩包都必須保持 warm，任何一次 release 都不逐出。
        private static void PingPongStaysWarm()
        {
            var p = new PmxCachePolicy();
            for (int i = 0; i < 4; i++)
            {
                p.Acquire("A");
                Check(p.Release("A").Count == 0, $"ping-pong round {i}: release A retains (no evict)");
                p.Acquire("B");
                Check(p.Release("B").Count == 0, $"ping-pong round {i}: release B retains (no evict)");
            }
            Check(p.IdleCount == 2, "ping-pong retains exactly A and B (within cap)");
        }

        // 上限行為：第 3 個進入 idle 的 entry 逐出最舊的（先 idle 的先走，僅一個）。
        private static void CapEvictsOldestIdle()
        {
            var p = new PmxCachePolicy();
            p.Acquire("A");
            var vA = p.Release("A");
            p.Acquire("B");
            var vB = p.Release("B");
            p.Acquire("C");
            var vC = p.Release("C");
            Check(vA.Count == 0 && vB.Count == 0, "first two idled entries retained");
            Check(vC.Count == 1 && vC[0] == "A", "third idle evicts exactly the OLDEST idle (A)");
            Check(p.IdleCount == 2, "cap keeps exactly IdleCap idle entries");
        }

        // 活著的（refcount>0）entry 永遠不會成為逐出對象；re-acquire 後的 A 不得被逐出。
        private static void ActiveEntryNeverEvicted()
        {
            var r = new PmxCachePolicy();
            r.Acquire("A");
            r.Release("A");
            r.Acquire("A");                       // A 回到 active
            r.Acquire("B");
            r.Release("B");                       // idle [B]
            r.Acquire("C");
            r.Release("C");                       // idle [B,C]
            r.Acquire("D");
            var v = r.Release("D");               // idle [B,C,D] → 逐出 B；A active 不在名單
            Check(v.Count == 1 && v[0] == "B", "re-acquired active entry is never an evict victim");
            Check(r.RefCount("A") == 1, "active entry keeps its refcount");
        }

        // 未被 Acquire 過的 key Release 是靜默 no-op（沿用舊行為，不得拋例外）。
        private static void UntrackedReleaseIsNoOp()
        {
            var p = new PmxCachePolicy();
            var v = p.Release("ghost");
            Check(v.Count == 0 && p.RefCount("ghost") == 0, "release of untracked key is a no-op");
        }

        // 組態覆寫：帶上限建構的 policy 以該上限為準——瀏覽更多角色也不逐出（切換不流暢的對策）。
        private static void CapOverrideKeepsMoreIdle()
        {
            var p = new PmxCachePolicy(5);
            for (int i = 0; i < 5; i++)
            {
                var key = "pkg" + i;
                p.Acquire(key);
                Check(p.Release(key).Count == 0, $"cap5: release {key} retains (within override cap)");
            }
            Check(p.IdleCount == 5, "cap5 keeps five idle entries");
            p.Acquire("extra");
            var v = p.Release("extra");
            Check(v.Count == 1 && v[0] == "pkg0", "sixth idle evicts exactly the OLDEST (pkg0)");
            Check(p.IdleCount == 5, "cap override keeps exactly 5 after evict");
            var pDefault = new PmxCachePolicy();
            pDefault.Acquire("A");
            pDefault.Release("A");
            pDefault.Acquire("B");
            pDefault.Release("B");
            pDefault.Acquire("C");
            var vC = pDefault.Release("C");
            Check(vC.Count == 1 && vC[0] == "A", "default policy keeps the pinned cap 2 behavior");
        }

        // 外部卸載資產（PurgeDeadEntry）時，Remove 必須同時清除 refcount 與 idle 佇列，讓後續重載乾淨重建。
        private static void RemovePurgesDeadEntry()
        {
            var p = new PmxCachePolicy(2);
            p.Acquire("A");
            p.Release("A");
            Check(p.IdleCount == 1, "A starts in idle queue");
            p.Remove("A");
            Check(p.IdleCount == 0 && p.RefCount("A") == 0, "Remove clears idle queue and refcount for purged key");
            p.Acquire("B");
            p.Remove("B");
            Check(p.RefCount("B") == 0 && p.IdleCount == 0, "Remove clears active key refcount");
        }

        // ReadName：只消費到名稱欄位結束，payload（PMX+貼圖）不被讀進來。
        private static void ReadNameSkipsPayload()
        {
            var blob = BuildFixtureContainer("測試角色");
            // 期望：magic 4 + version 4 + len 4 + utf8 名稱 = 12 + nameLen
            int nameLen = Encoding.UTF8.GetByteCount("測試角色");
            int expectedPos = 12 + nameLen;

            string name;
            using (var ms = new MemoryStream(blob))
            {
                name = PmxContainer.ReadName(ms);
                Check(name == "測試角色", "ReadName returns the container display name");
                Check(ms.Position == expectedPos, $"ReadName stops after the name field (pos {ms.Position}, want {expectedPos}; payload {blob.Length - expectedPos}u bytes NOT consumed)");
            }

            // 名稱與全量 Read 一致（同一份 blob 兩種讀法不得分歧）
            using (var ms = new MemoryStream(blob))
            {
                var full = PmxContainer.Read(ms);
                Check(full.Name == name, "full Read name matches ReadName");
                Check(full.Pmx.Length == 4 && full.Textures.Count == 1, "full Read roundtrips payload");
            }
        }

        // 壞 magic 必須與 Read 同樣拋 InvalidDataException（不回垃圾名稱）。
        private static void ReadNameRejectsBadMagic()
        {
            using (var ms = new MemoryStream(new byte[] { 0x42, 0x41, 0x44, 0x21, 0, 0, 0, 0 }))
            {
                try
                {
                    PmxContainer.ReadName(ms);
                    Check(false, "bad magic must throw InvalidDataException");
                }
                catch (InvalidDataException)
                {
                    // expected
                }
            }
        }

        private static byte[] BuildFixtureContainer(string name)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8, false))
            {
                w.Write(Encoding.ASCII.GetBytes("GFCP"));
                w.Write(PmxContainer.FormatVersion);
                WriteStr(w, name);
                WriteStr(w, "model.pmx");
                var pmxPayload = new byte[] { 1, 2, 3, 4 };
                w.Write(pmxPayload.Length);
                w.Write(pmxPayload);
                w.Write(1);
                WriteStr(w, "tex.png");
                var tex = new byte[] { 9, 8, 7, 6 };
                w.Write(tex.Length);
                w.Write(tex);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write(b.Length);
            w.Write(b);
        }
    }
}
