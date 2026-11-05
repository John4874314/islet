// StashProbe - verify the stash data layer. Drag-drop feel must be tested on a real desktop.
// Exit 0 = all pass, non-zero = failures.
//
// NOTE: keep this file pure ASCII. csc.exe reads source as ANSI by default;
// non-ASCII comments are fine but non-ASCII string literals get mangled.

using System;
using System.Collections.Generic;
using System.IO;
using Islet.App.Services;

internal static class StashProbe
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> TempFiles = new List<string>();

    // DataObject / drag-drop COM needs STA. Real WPF UI thread is STA already.
    [STAThread]
    private static int Main()
    {
        // NOTE: the restricted session denies creating NEW dirs under %TEMP%
        // and %LOCALAPPDATA%. Use a dir inside the workspace, which is writable.
        string work = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-work");
        try { Directory.CreateDirectory(work); } catch { }

        try
        {
            Console.WriteLine("work dir: " + work);

            // Use a writable subdir: the restricted session denies creating
            // new dirs under %LOCALAPPDATA%. Production uses the no-arg ctor.
            string stashDir = Path.Combine(work, "stash");
            StashStore store = new StashStore(stashDir);
            Console.WriteLine("stash dir: " + store.DirectoryPath);
            Console.WriteLine("available: " + store.Available);
            Console.WriteLine();

            // 1. copy a file, original untouched
            string a = MakeFile(work, "report.txt", "hello-islet");
            List<string> errs = new List<string>();
            IList<StashItem> added = store.Add(new[] { a }, errs);

            Check("one file added", added.Count == 1);
            Check("count == 1", store.Count == 1);
            Check("no errors", errs.Count == 0);
            if (added.Count == 1)
            {
                Check("copy exists", File.Exists(added[0].Path));
                Check("copy content matches", File.ReadAllText(added[0].Path) == "hello-islet");
                Check("copy outside source dir",
                    !string.Equals(Path.GetDirectoryName(added[0].Path), work,
                                   StringComparison.OrdinalIgnoreCase));
                Check("display name kept", added[0].Name == "report.txt");
            }
            Check("original still exists", File.Exists(a));
            Check("original unchanged", File.ReadAllText(a) == "hello-islet");
            Console.WriteLine();

            // 2. same name from another dir must not overwrite
            string sub = Path.Combine(work, "sub");
            Directory.CreateDirectory(sub);
            string b2 = MakeFile(sub, "report.txt", "SECOND");
            IList<StashItem> added2 = store.Add(new[] { b2 }, null);
            Check("same-name add ok", added2.Count == 1);
            if (added.Count == 1 && added2.Count == 1)
                Check("two copies have different paths", added[0].Path != added2[0].Path);
            Check("count == 2", store.Count == 2);
            Console.WriteLine();

            // 3. reject missing path and directory
            List<string> errs3 = new List<string>();
            IList<StashItem> added3 = store.Add(
                new[] { Path.Combine(work, "missing.txt"), sub }, errs3);
            Check("invalid paths rejected", added3.Count == 0);
            Check("errors reported", errs3.Count >= 1);
            Check("count still 2", store.Count == 2);
            Console.WriteLine();

            // 4. Remove deletes only the copy
            IList<StashItem> snap = store.Snapshot();
            string copyToRemove = snap[0].Path;
            bool removed = store.Remove(snap[0].Id);
            Check("Remove returns true", removed);
            Check("copy deleted", !File.Exists(copyToRemove));
            Check("original still exists", File.Exists(a));
            Check("count == 1", store.Count == 1);
            Check("double Remove returns false", !store.Remove(snap[0].Id));
            Console.WriteLine();

            // 5. Clear
            IList<StashItem> beforeClear = store.Snapshot();
            string lastCopy = beforeClear[0].Path;
            store.Clear();
            Check("Clear -> count 0", store.Count == 0);
            Check("Clear deletes copy", !File.Exists(lastCopy));
            Check("originals intact", File.Exists(a) && File.Exists(b2));
            Console.WriteLine();

            // 6. restart clears leftovers
            string leftover = MakeFile(work, "leftover.txt", "leftover");
            IList<StashItem> left = store.Add(new[] { leftover }, null);
            Check("leftover created", left.Count == 1 && File.Exists(left[0].Path));
            string leftoverCopy = left[0].Path;
            store = new StashStore(stashDir);
            Check("new store count 0", store.Count == 0);
            Check("new store removed leftover copy", !File.Exists(leftoverCopy));
            Check("leftover original intact", File.Exists(leftover));
            Console.WriteLine();

            // 7. drop source is a real readable file
            string f = MakeFile(work, "for-friend.txt", "send-me");
            IList<StashItem> forDrop = store.Add(new[] { f }, null);
            Check("drop sample added", forDrop.Count == 1);
            if (forDrop.Count == 1)
            {
                Check("drop source exists", File.Exists(forDrop[0].Path));
                Check("drop source readable", File.ReadAllText(forDrop[0].Path) == "send-me");
            }
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex.GetType().FullName + " " + ex.Message);
            _fail++;
        }
        finally
        {
            foreach (string f in TempFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine("----------------------------------------");
        Console.WriteLine("PASS=" + _pass + "  FAIL=" + _fail);
        return _fail == 0 ? 0 : 1;
    }

    private static string MakeFile(string dir, string name, string content)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        if (!TempFiles.Contains(p)) TempFiles.Add(p);
        return p;
    }

    private static void Check(string label, bool ok)
    {
        Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + label);
        if (ok) _pass++; else _fail++;
    }
}
