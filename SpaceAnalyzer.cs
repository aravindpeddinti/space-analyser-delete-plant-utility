// =====================================================================
//  Space Analyzer  -  a disk space analyzer for Windows VMs
//  Built with Windows' own C# compiler (see Build.bat). No installs needed.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("Space Analyzer")]
[assembly: AssemblyProduct("Space Analyzer")]
[assembly: AssemblyVersion("3.3.0.0")]
[assembly: AssemblyFileVersion("3.3.0.0")]

namespace SpaceAnalyzer
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)(3072 | 12288); }
            catch { try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; } catch { } }

            // Headless mode: when started with /report, do the work with no window
            // and write a results file. This is what the "deploy and run" feature
            // will use on each VM. You can also run it by hand to test, e.g.:
            //   SpaceAnalyzer.exe /report C:\Temp\report.txt /drives C:,D: /advisor
            if (args != null && args.Length > 0 && (HasFlag(args, "/report") || HasFlag(args, "/agent") || HasFlag(args, "/delete") || HasFlag(args, "/cleanup") || HasFlag(args, "/listdir") || HasFlag(args, "/scheduled") || HasFlag(args, "/recyclelist") || HasFlag(args, "/recycledel")))
                return Headless.Run(args);

            try { Log.Path1 = System.IO.Path.Combine(Application.StartupPath, "SpaceAnalyzer_Debug.log"); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Space Analyzer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { MessageBox.Show("Something went wrong:\n\n" + (e.ExceptionObject as Exception != null ? ((Exception)e.ExceptionObject).Message : "unknown error"),
                    "Space Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            };
            Application.Run(new MainForm());
            return 0;
        }

        static bool HasFlag(string[] a, string f)
        {
            foreach (string s in a) if (s.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // ------------------------------------------------------------------
    //  Colors and fonts used everywhere
    // ------------------------------------------------------------------
    // ==================================================================
    //  HEADLESS MODE: scan + advisor with no window, write a report file.
    //  Used by "deploy and run"; also runnable by hand for testing.
    // ==================================================================
    static class Headless
    {
        public static int Run(string[] args)
        {
            if (HasFlag(args, "/scheduled")) return RunScheduled(args);
            if (HasFlag(args, "/recyclelist")) return RunRecycleList(args);
            if (HasFlag(args, "/recycledel")) return RunRecycleDel(args);
            if (HasFlag(args, "/delete")) return RunDelete(args);
            if (HasFlag(args, "/cleanup")) return RunCleanup(args);
            if (HasFlag(args, "/listdir")) return RunListDir(args);
            if (HasFlag(args, "/agent")) return RunAgent(args);
            try
            {
                string outPath = Arg(args, "/report");
                if (string.IsNullOrEmpty(outPath)) { return 2; }
                bool fast = !HasFlag(args, "/slow");
                bool advisor = HasFlag(args, "/advisor");
                List<string> drives = DriveList(Arg(args, "/drives"));

                var sb = new StringBuilder();
                sb.AppendLine("Space Analyzer report");
                sb.AppendLine("Computer:  " + Environment.MachineName);
                sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(new string('=', 60));

                var scanner = new Scanner();
                scanner.UseFast = fast;
                DateTime t0 = DateTime.Now;
                DirNode root = scanner.Run(drives);
                double secs = (DateTime.Now - t0).TotalSeconds;
                sb.AppendLine(string.Format("Scanned {0}: {1:N0} files, {2}, in {3:0.0}s{4}",
                    string.Join(", ", drives), root.FileCount, Util.Size(root.Size), secs,
                    scanner.FastDrives > 0 ? " (fast disk index)" : ""));
                sb.AppendLine();

                sb.AppendLine("TOP 30 FOLDERS");
                foreach (DirNode c in Flatten(root).OrderByDescending(n => n.Size).Take(30))
                    sb.AppendLine(string.Format("  {0,10}  {1}", Util.Size(c.Size), c.FullPath));
                sb.AppendLine();

                sb.AppendLine("TOP 30 FILES");
                foreach (FileEntry f in scanner.TopFiles.OrderByDescending(x => x.Size).Take(30))
                    sb.AppendLine(string.Format("  {0,10}  {1}", Util.Size(f.Size), f.FullPath));
                sb.AppendLine();

                sb.AppendLine("SPACE BY FILE TYPE (top 20)");
                foreach (ExtStat x in scanner.Exts.Values.OrderByDescending(e => e.Size).Take(20))
                    sb.AppendLine(string.Format("  {0,10}  {1}  ({2:N0} files)", Util.Size(x.Size), x.Ext, x.Count));
                sb.AppendLine();

                if (advisor)
                {
                    sb.AppendLine("CLEANUP ADVISOR");
                    var rules = CleanupRules.Build();
                    new Advisor { UseIndex = fast }.Analyze(rules, scanner.TopFiles.Concat(scanner.BigFiles).ToList());
                    long safe = 0;
                    foreach (CleanupRule r in rules)
                    {
                        if (r.Kind == RuleKind.Info) continue;
                        string sz = r.Kind == RuleKind.ComponentCleanup ? "varies" : Util.Size(r.Size);
                        sb.AppendLine(string.Format("  [{0}] {1,10}  {2}", r.Level, sz, r.Title));
                        if (r.Level == Safety.Safe && r.Kind != RuleKind.ComponentCleanup) safe += r.Size;
                    }
                    sb.AppendLine(string.Format("  Safe to free now: {0}", Util.Size(safe)));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Arg(args, "/report") ?? "SpaceAnalyzer_error.txt", "ERROR: " + ex.Message); } catch { }
                return 1;
            }
        }

        // Structured output the controlling PC reads back to fill the normal tabs.
        public const long FolderThreshold = 2L * 1024 * 1024;   // list folders >= 2 MB

        static int RunAgent(string[] args)
        {
            if (HasFlag(args, "/delete")) return RunDelete(args);
            if (HasFlag(args, "/cleanup")) return RunCleanup(args);
            if (HasFlag(args, "/listdir")) return RunListDir(args);
            string outPath = Arg(args, "/agent");
            if (string.IsNullOrEmpty(outPath)) return 2;
            string logPath = outPath + ".log";
            Log.To(logPath, "AGENT START on " + Environment.MachineName + " as " + Environment.UserName +
                   " | elevated=" + IsElevated() + " | args=" + string.Join(" ", args));
            try
            {
                bool fast = !HasFlag(args, "/slow");
                List<string> drives = DriveList(Arg(args, "/drives"));
                Log.To(logPath, "Drives to scan: " + string.Join(",", drives) + " | fast=" + fast);
                var scanner = new Scanner { UseFast = fast };
                DateTime t0 = DateTime.Now;
                DirNode root = scanner.Run(drives);
                double secs = (DateTime.Now - t0).TotalSeconds;
                Log.To(logPath, string.Format("Scan done: {0} files, {1} bytes, {2:0.0}s, fastDrives={3}, slowDrives={4}",
                    scanner.Files, scanner.Bytes, secs, scanner.FastDrives, scanner.SlowDrives));

                var sb = new StringBuilder();
                sb.Append("SA1\n");
                sb.Append("HOST\t").Append(Environment.MachineName).Append('\n');
                sb.Append("SCAN\t").Append(scanner.Files).Append('\t').Append(scanner.Bytes)
                  .Append('\t').Append(secs.ToString("0.0", CultureInfo.InvariantCulture))
                  .Append('\t').Append(scanner.FastDrives).Append('\t').Append(scanner.SlowDrives).Append('\n');

                foreach (string dl in drives)
                {
                    try { var di = new DriveInfo(dl); if (di.IsReady) sb.Append("DV\t").Append(di.Name.TrimEnd('\\')).Append('\t').Append(di.TotalSize).Append('\t').Append(di.TotalFreeSpace).Append('\n'); }
                    catch { }
                }

                WriteFolders(sb, root, 0);

                foreach (FileEntry f in scanner.TopFiles.OrderByDescending(x => x.Size))
                    sb.Append("XF\t").Append(f.Size).Append('\t')
                      .Append(f.Modified == DateTime.MinValue ? 0 : f.Modified.ToFileTime()).Append('\t')
                      .Append(f.Ext).Append('\t').Append(Clean(f.FullPath)).Append('\n');

                foreach (ExtStat x in scanner.Exts.Values.OrderByDescending(e => e.Size))
                    sb.Append("XE\t").Append(x.Size).Append('\t').Append(x.Count).Append('\t').Append(x.Ext).Append('\n');

                var rules = CleanupRules.Build();
                new Advisor { UseIndex = fast }.Analyze(rules, scanner.TopFiles.Concat(scanner.BigFiles).ToList());
                foreach (string ini in IniFinder.FindFixedDrives())
                    sb.Append("INI\t").Append(Clean(ini)).Append('\n');
                int adId = 0;
                foreach (CleanupRule r in rules)
                {
                    sb.Append("AD\t").Append(adId).Append('\t').Append((int)r.Level).Append('\t').Append((int)r.Kind).Append('\t')
                      .Append(r.Size).Append('\t').Append(r.Hits.Count).Append('\t').Append(Clean(r.Title)).Append('\n');
                    if (r.IsFileList)
                    {
                        int cap = 0;
                        foreach (FileHit h in r.Hits.OrderByDescending(x => x.Size))
                        {
                            if (cap++ >= 5000) break;   // keep the result file reasonable
                            sb.Append("AF\t").Append(adId).Append('\t').Append(h.Size).Append('\t')
                              .Append(h.Modified == DateTime.MinValue ? 0 : h.Modified.ToFileTime()).Append('\t')
                              .Append(Clean(h.Path)).Append('\n');
                        }
                    }
                    adId++;
                }

                Log.To(logPath, "Report built, " + sb.Length + " chars. Writing to " + outPath);
                string full = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                string tmp = full + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(full)) File.Delete(full);
                File.Move(tmp, full);   // appears complete in one step
                Log.To(logPath, "AGENT DONE ok");
                return 0;
            }
            catch (Exception ex)
            {
                Log.To(logPath, "AGENT ERROR: " + ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace);
                try { File.WriteAllText(outPath + ".err", ex.GetType().Name + ": " + ex.Message); } catch { }
                return 1;
            }
        }

        static bool IsElevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        // Deletes a list of files/folders named in a job file (one path per line).
        // Writes "<freedBytes>\t<deletedCount>\t<skippedCount>" to <job>.done
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetFileAttributesW(string path, uint attributes);

        // One direct call to Windows per file; clears read-only and retries once if needed.
        static bool AgentDelete(string path)
        {
            string p = path.Length >= 240 && !path.StartsWith("\\\\") ? "\\\\?\\" + path : path;
            if (DeleteFileW(p)) return true;
            int err = Marshal.GetLastWin32Error();
            if (err == 2 || err == 3) return true;              // already gone
            if (err == 5 && SetFileAttributesW(p, 0x80) && DeleteFileW(p)) return true;  // was read-only
            return false;
        }

        // ===== Scheduled maintenance run (headless, started by Windows Task Scheduler) =====
        // Config file lists the VMs; for each, it scans over the network (file-by-file, since
        // this runs unattended), works out old files per owner folder, and emails each owner.
        static int RunScheduled(string[] args)
        {
            string log = Path.Combine(Application.StartupPath, "SpaceAnalyzer_Schedule.log");
            try
            {
                Log.To(log, "SCHEDULED RUN START as " + Environment.UserName);
                int ageDays = 180; int.TryParse(Arg(args, "/age") ?? "180", out ageDays);
                var vms = ScheduleConfig.LoadVms();
                string omErr; var owners = OwnerConfig.LoadMap(out omErr);
                var email = OwnerConfig.LoadEmail();
                string from = email.ContainsKey("From") ? email["From"] : "";
                string server = email.ContainsKey("SmtpServer") ? email["SmtpServer"] : "";
                Log.To(log, "VMs=" + vms.Count + " owners=" + owners.Count + " smtp=" + server);

                if (owners.Count == 0) { Log.To(log, "No OwnerMap.csv entries; nothing to email."); return 0; }

                // Build the set of owner folders to measure, grouped by which VM they live on.
                var scanner = new OwnerScanner(ageDays);
                // For scheduled runs we scan the owner folders directly over UNC on each VM.
                foreach (string vm in vms)
                {
                    Log.To(log, "Processing VM " + vm);
                    // Map each owner folder onto this VM via its admin share if the path is local (C:\..)
                    var vmOwners = new List<OwnerEntry>();
                    foreach (OwnerEntry o in owners)
                    {
                        string unc = ToUnc(vm, o.Folder);
                        if (unc == null) continue;
                        vmOwners.Add(new OwnerEntry { Folder = unc, Email = o.Email, Name = o.Name });
                    }
                    if (vmOwners.Count == 0) { Log.To(log, "  no owner folders map to this VM"); continue; }
                    scanner.Scan(vmOwners);

                    if (server.Length > 0 && from.IndexOf('@') > 0)
                    {
                        int port = 25; int.TryParse(email.ContainsKey("Port") ? email["Port"] : "25", out port);
                        bool ssl = email.ContainsKey("UseSsl") && email["UseSsl"].Equals("true", StringComparison.OrdinalIgnoreCase);
                        string user = email.ContainsKey("Username") ? email["Username"] : "";
                        string pass = email.ContainsKey("Password") ? email["Password"] : "";
                        using (var smtp = new System.Net.Mail.SmtpClient(server, port))
                        {
                            smtp.EnableSsl = ssl; smtp.Timeout = 30000;
                            if (user.Length > 0) smtp.Credentials = new System.Net.NetworkCredential(user, pass);
                            foreach (OwnerEntry o in vmOwners.Where(x => x.Files.Count > 0))
                            {
                                try { using (var m = OwnerMail.Build(o, ageDays, from, false)) smtp.Send(m); Log.To(log, "  emailed " + o.Email + " (" + o.Files.Count + " files)"); }
                                catch (Exception ex) { Log.To(log, "  email failed " + o.Email + ": " + ex.Message); }
                            }
                        }
                    }
                    else Log.To(log, "  SMTP not configured; skipped sending. Fill EmailSettings.ini.");
                }
                Log.To(log, "SCHEDULED RUN DONE");
                return 0;
            }
            catch (Exception ex) { Log.To(log, "SCHEDULED ERROR: " + ex.Message); return 1; }
        }

        // Turn a local path on a VM (C:\Users\bob) into its admin-share UNC (\\VM\C$\Users\bob).
        static string ToUnc(string vm, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("\\\\")) return path;            // already a network path
            if (path.Length >= 2 && path[1] == ':')
                return "\\\\" + vm + "\\" + path[0] + "$" + path.Substring(2);
            return null;
        }

        // Lists Recycle Bin items on this machine. Writes lines: size\tfileTime\tfolder\toriginalPath\tdataPath\tisFolder
        static int RunRecycleList(string[] args)
        {
            string job = Arg(args, "/recyclelist");
            if (string.IsNullOrEmpty(job)) return 2;
            try
            {
                var items = RecycleBin.Read(() => false);
                var sb = new StringBuilder();
                foreach (BinItem it in items)
                    sb.Append(it.Size).Append('\t').Append(it.Deleted == DateTime.MinValue ? 0 : it.Deleted.ToFileTime()).Append('\t')
                      .Append(Clean(it.OriginalPath)).Append('\t').Append(Clean(it.InfoPath)).Append('\t').Append(Clean(it.DataPath))
                      .Append('\t').Append(it.IsFolder ? 1 : 0).Append('\n');
                File.WriteAllText(job + ".done", sb.ToString());
                return 0;
            }
            catch (Exception ex) { try { File.WriteAllText(job + ".err", ex.Message); } catch { } return 1; }
        }

        // Permanently removes Recycle Bin items. Job lines: infoPath\tdataPath  (empty infoPath = empty whole bin)
        static int RunRecycleDel(string[] args)
        {
            string job = Arg(args, "/recycledel");
            if (string.IsNullOrEmpty(job)) return 2;
            try
            {
                long freed = 0; int del = 0;
                string[] lines = File.ReadAllLines(job);
                if (lines.Length == 1 && lines[0].Trim() == "*EMPTYALL*")
                {
                    Recycler.EmptyRecycleBin(IntPtr.Zero);
                    File.WriteAllText(job + ".done", "0\t0\t0");
                    return 0;
                }
                foreach (string raw in lines)
                {
                    string[] p = raw.Split('\t');
                    string info = p.Length > 0 ? p[0] : "";
                    string data = p.Length > 1 ? p[1] : "";
                    try
                    {
                        if (!string.IsNullOrEmpty(data))
                        {
                            if (Directory.Exists(data)) { Directory.Delete(data, true); del++; }
                            else if (File.Exists(data)) { long sz = 0; try { sz = new FileInfo(data).Length; } catch { } File.Delete(data); freed += sz; del++; }
                        }
                        if (!string.IsNullOrEmpty(info) && File.Exists(info)) File.Delete(info);
                    }
                    catch { }
                }
                File.WriteAllText(job + ".done", freed + "\t" + del + "\t0");
                return 0;
            }
            catch (Exception ex) { try { File.WriteAllText(job + ".err", ex.Message); } catch { } return 1; }
        }

        // Lists the files directly inside one folder (not subfolders). Job file: first line = folder path.
        // Writes "<size>\t<fileTime>\t<fullpath>" lines to <job>.done
        static int RunListDir(string[] args)
        {
            string job = Arg(args, "/listdir");
            if (string.IsNullOrEmpty(job)) return 2;
            try
            {
                string folder = File.ReadAllLines(job).FirstOrDefault(l => l.Trim().Length > 0);
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    foreach (string f in Directory.EnumerateFiles(folder))
                    {
                        long sz = 0; long ft = 0;
                        try { var fi = new FileInfo(f); sz = fi.Length; ft = fi.LastWriteTime.ToFileTime(); } catch { }
                        sb.Append(sz).Append('\t').Append(ft).Append('\t').Append(Clean(f)).Append('\n');
                    }
                }
                File.WriteAllText(job + ".done", sb.ToString());
                return 0;
            }
            catch (Exception ex) { try { File.WriteAllText(job + ".err", ex.Message); } catch { } return 1; }
        }

        static int RunDelete(string[] args)
        {
            string job = Arg(args, "/delete");
            if (string.IsNullOrEmpty(job)) return 2;
            try
            {
                long freed = 0; int del = 0, skip = 0;
                var files = new List<string>();
                var folders = new List<string>();
                foreach (string raw in File.ReadAllLines(job))
                {
                    string path = raw.Trim();
                    if (path.Length == 0) continue;
                    if (Util.IsProtected(path)) { Interlocked.Increment(ref skip); continue; }
                    if (Directory.Exists(path)) folders.Add(path);
                    else if (File.Exists(path)) files.Add(path);
                }

                // Files: delete many at once with a direct Windows call (same fast path as local cleanup).
                var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount * 2)) };
                System.Threading.Tasks.Parallel.ForEach(files, opts, path =>
                {
                    long sz = 0; try { sz = new FileInfo(path).Length; } catch { }
                    if (AgentDelete(path)) { Interlocked.Add(ref freed, sz); Interlocked.Increment(ref del); }
                    else Interlocked.Increment(ref skip);
                });

                // Folders: sequential (recursive deletes shouldn't overlap).
                foreach (string path in folders)
                {
                    try
                    {
                        long sz = DirSize(path);
                        Directory.Delete(path, true);
                        if (!Directory.Exists(path)) { freed += sz; del++; } else skip++;
                    }
                    catch { skip++; }
                }
                File.WriteAllText(job + ".done", freed + "\t" + del + "\t" + skip);
                return 0;
            }
            catch (Exception ex) { try { File.WriteAllText(job + ".err", ex.Message); } catch { } return 1; }
        }

        // Runs the Cleanup Advisor on the VM and cleans the categories named in the job
        // file (one rule title per line). Writes freed/deleted/skipped to <job>.done
        static int RunCleanup(string[] args)
        {
            string job = Arg(args, "/cleanup");
            if (string.IsNullOrEmpty(job)) return 2;
            try
            {
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(job)) { string t = raw.Trim(); if (t.Length > 0) wanted.Add(t); }
                var rules = CleanupRules.Build();
                var adv = new Advisor { UseIndex = !HasFlag(args, "/slow") };
                adv.Analyze(rules, null);
                var todo = rules.Where(r => wanted.Contains(r.Title) && r.Analyzed && r.HasWork).ToList();
                foreach (CleanupRule r in todo) r.Checked = true;
                adv.Clean(todo);
                long freed = todo.Sum(r => r.Freed);
                int del = todo.Sum(r => r.Deleted), skip = todo.Sum(r => r.Skipped);
                File.WriteAllText(job + ".done", freed + "\t" + del + "\t" + skip);
                return 0;
            }
            catch (Exception ex) { try { File.WriteAllText(job + ".err", ex.Message); } catch { } return 1; }
        }

        static long DirSize(string path)
        {
            long t = 0;
            try { foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { try { t += new FileInfo(f).Length; } catch { } } }
            catch { }
            return t;
        }

        // Emits folders whose total size is at least the threshold, pre-order with depth.
        static void WriteFolders(StringBuilder sb, DirNode n, int depth)
        {
            sb.Append("F\t").Append(depth).Append('\t').Append(n.Size).Append('\t').Append(n.FileCount)
              .Append('\t').Append(n.OwnSize).Append('\t').Append(n.OwnCount)
              .Append('\t').Append(Clean(n.IsVirtual ? "" : n.FullPath)).Append('\n');
            foreach (DirNode c in n.Children)
                if (c.Size >= FolderThreshold) WriteFolders(sb, c, depth + 1);
        }

        static IEnumerable<DirNode> Flatten(DirNode root)
        {
            var stack = new Stack<DirNode>();
            foreach (DirNode c in root.Children) stack.Push(c);
            while (stack.Count > 0)
            {
                DirNode n = stack.Pop();
                yield return n;
                foreach (DirNode c in n.Children) stack.Push(c);
            }
        }

        static List<string> DriveList(string spec)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(spec))
                foreach (string d in spec.Split(',', ';'))
                {
                    string x = d.Trim().TrimEnd('\\', ':');
                    if (x.Length >= 1) list.Add(x + ":\\");
                }
            if (list.Count == 0)
                foreach (DriveInfo di in DriveInfo.GetDrives())
                    try { if (di.IsReady && di.DriveType == DriveType.Fixed) list.Add(di.RootDirectory.FullName); } catch { }
            return list;
        }

        static string Arg(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }

        static bool HasFlag(string[] a, string f)
        {
            foreach (string s in a) if (s.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(238, 241, 245);
        public static readonly Color Surface = Color.White;
        public static readonly Color SurfaceAlt = Color.FromArgb(246, 248, 251);
        public static readonly Color Border = Color.FromArgb(218, 223, 231);
        public static readonly Color Track = Color.FromArgb(230, 234, 240);
        public static readonly Color Ink = Color.FromArgb(26, 36, 51);
        public static readonly Color Muted = Color.FromArgb(103, 116, 137);
        public static readonly Color Faint = Color.FromArgb(160, 169, 184);
        public static readonly Color Accent = Color.FromArgb(41, 82, 204);
        public static readonly Color AccentSoft = Color.FromArgb(228, 235, 251);
        public static readonly Color Danger = Color.FromArgb(214, 69, 69);
        public static readonly Color Warn = Color.FromArgb(224, 152, 46);
        public static readonly Color Gray = Color.FromArgb(150, 160, 176);

        public static readonly Color[] Palette = {
            Color.FromArgb(41, 82, 204),  Color.FromArgb(20, 150, 140),  Color.FromArgb(224, 152, 46),
            Color.FromArgb(214, 69, 103), Color.FromArgb(124, 77, 204),  Color.FromArgb(46, 158, 106),
            Color.FromArgb(56, 152, 224), Color.FromArgb(232, 110, 50),  Color.FromArgb(160, 60, 140),
            Color.FromArgb(120, 145, 40), Color.FromArgb(90, 110, 140),  Color.FromArgb(180, 80, 60)
        };

        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font BodyBold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font SmallBold = new Font("Segoe UI Semibold", 8.5f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 16f);
        public static readonly Font Big = new Font("Segoe UI Semibold", 13f);
        public static readonly Font DriveFont = new Font("Segoe UI Semibold", 17f);

        public static Color Pick(int i) { return Palette[i % Palette.Length]; }

        public static Color Lighten(Color c, float a)
        {
            return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * a), (int)(c.G + (255 - c.G) * a), (int)(c.B + (255 - c.B) * a));
        }

        public static Color Darken(Color c, float a)
        {
            return Color.FromArgb(c.A, (int)(c.R * (1 - a)), (int)(c.G * (1 - a)), (int)(c.B * (1 - a)));
        }

        public static Color UsageColor(double pct)
        {
            if (pct >= 90) return Danger;
            if (pct >= 75) return Warn;
            return Accent;
        }
    }

    // ------------------------------------------------------------------
    //  Small helpers
    // ------------------------------------------------------------------
    // Simple log file. The controller logs next to the .exe; the agent logs on the VM.
    static class Log
    {
        static readonly object gate = new object();
        public static string Path1;   // set at startup

        public static void To(string path, string msg)
        {
            try
            {
                lock (gate)
                    System.IO.File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        public static void Line(string msg)
        {
            if (string.IsNullOrEmpty(Path1))
            {
                try { Path1 = System.IO.Path.Combine(Application.StartupPath, "SpaceAnalyzer_Debug.log"); } catch { Path1 = "SpaceAnalyzer_Debug.log"; }
            }
            To(Path1, msg);
        }
    }

    static class Util
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        public static string Size(long b)
        {
            double v = b;
            if (v >= 1099511627776.0) return (v / 1099511627776.0).ToString("0.00") + " TB";
            if (v >= 1073741824.0) return (v / 1073741824.0).ToString("0.00") + " GB";
            if (v >= 1048576.0) return (v / 1048576.0).ToString("0.0") + " MB";
            if (v >= 1024.0) return (v / 1024.0).ToString("0") + " KB";
            return b + " B";
        }

        static readonly string[] ProtectedFolders = {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft")
        };
        static readonly string[] ProtectedNames = { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT" };
        static readonly string[] ProtectedParts = { "\\System Volume Information", "\\$Recycle.Bin", "\\Recovery", "\\Boot" };

        // Returns true for Windows system locations the app must never delete.
        public static bool IsProtected(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string full = path.TrimEnd('\\');
            if (full.Length <= 2) return true; // a drive root such as C:

            foreach (string p in ProtectedFolders)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (full.Equals(p, OIC) || full.StartsWith(p + "\\", OIC)) return true;
            }

            string name = Path.GetFileName(full) ?? "";
            foreach (string n in ProtectedNames)
                if (name.Equals(n, OIC)) return true;

            foreach (string part in ProtectedParts)
            {
                int i = full.IndexOf(part, OIC);
                if (i >= 0)
                {
                    int end = i + part.Length;
                    if (end == full.Length || full[end] == '\\') return true;
                }
            }

            // Don't allow deleting C:\Users or a whole user profile folder
            try
            {
                string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (!string.IsNullOrEmpty(users))
                {
                    if (full.Equals(users, OIC)) return true;
                    string parent = Path.GetDirectoryName(full);
                    if (parent != null && parent.Equals(users, OIC)) return true;
                }
            }
            catch { }
            return false;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void OpenInExplorer(string path, bool selectItem)
        {
            try
            {
                if (selectItem) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        public static string Csv(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }
    }

    // ------------------------------------------------------------------
    //  Recycle Bin (Windows shell)
    // ------------------------------------------------------------------
    static class Recycler
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);

        const uint FO_DELETE = 3;
        const ushort FOF_ALLOWUNDO = 0x40;          // send to Recycle Bin
        const ushort FOF_NOCONFIRMATION = 0x10;     // we already asked the user
        const ushort FOF_WANTNUKEWARNING = 0x4000;  // warn if too big for the Recycle Bin

        public static void MoveToRecycleBin(IWin32Window owner, IList<string> paths)
        {
            if (paths.Count == 0) return;
            var op = new SHFILEOPSTRUCT();
            op.hwnd = owner == null ? IntPtr.Zero : owner.Handle;
            op.wFunc = FO_DELETE;
            op.pFrom = string.Join("\0", paths) + "\0\0";
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING);
            SHFileOperation(ref op);
        }

        public static void EmptyRecycleBin(IWin32Window owner)
        {
            SHEmptyRecycleBin(owner == null ? IntPtr.Zero : owner.Handle, null, 0);
        }

        public static void EmptyRecycleBin(IntPtr hwnd)
        {
            SHEmptyRecycleBin(hwnd, null, 0);
        }
    }

    // ------------------------------------------------------------------
    //  Deletion log (CSV next to the .exe)
    // ------------------------------------------------------------------
    static class DeleteLog
    {
        public static string FilePath
        {
            get { return Path.Combine(Application.StartupPath, "SpaceAnalyzer_Log.csv"); }
        }

        public static void Write(string path, long size, string result)
        {
            try
            {
                bool isNew = !File.Exists(FilePath);
                using (var w = new StreamWriter(FilePath, true, Encoding.UTF8))
                {
                    if (isNew) w.WriteLine("Time,User,Computer,Path,SizeMB,Result");
                    w.WriteLine(string.Join(",",
                        Util.Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                        Util.Csv(Environment.UserName),
                        Util.Csv(Environment.MachineName),
                        Util.Csv(path),
                        (size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture),
                        Util.Csv(result)));
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    //  Scan results
    // ------------------------------------------------------------------
    class DirNode
    {
        public string Name;
        public string FullPath;
        public long Size, FileCount;     // including all subfolders
        public long OwnSize, OwnCount;   // files directly inside this folder
        public DirNode Parent;
        public List<DirNode> Children = new List<DirNode>();
        public bool IsVirtual;
    }

    class FileEntry
    {
        public string Name, FullPath, Ext;
        public long Size;
        public DateTime Modified;
        public DirNode Folder;
    }

    class ExtStat
    {
        public string Ext;
        public long Size, Count;
    }

    class RowItem
    {
        public string Name;
        public long Size, Count;
        public DirNode Node;
        public bool IsFiles;
        public Color Color;
    }

    // ------------------------------------------------------------------
    //  The scanner (runs in the background)
    // ------------------------------------------------------------------
    class Scanner
    {
        public const int TopN = 500;
        public const long BigFileMin = 10L * 1024 * 1024;   // files kept for the duplicate finder
        public volatile bool Cancel;
        public bool UseFast = true;
        public int FastDrives, SlowDrives;
        public long Files, Bytes, Denied;
        public volatile string Current = "";
        public List<FileEntry> TopFiles = new List<FileEntry>();
        public List<FileEntry> BigFiles = new List<FileEntry>();
        public Dictionary<string, ExtStat> Exts = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        public List<string> IniPaths = new List<string>();
        HashSet<string> iniPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long topMin = long.MaxValue;
        int topMinIdx = -1;

        public DirNode Run(List<string> roots)
        {
            if (roots.Count == 1) return ScanRoot(roots[0], null);

            var root = new DirNode { Name = "All selected drives", FullPath = "", IsVirtual = true };
            foreach (string r in roots)
            {
                if (Cancel) break;
                DirNode n = ScanRoot(r, root);
                root.Children.Add(n);
                root.Size += n.Size;
                root.FileCount += n.FileCount;
            }
            root.Children.Sort(BySize);
            return root;
        }

        // Try the fast NTFS index scan first; if anything goes wrong, use the normal scan.
        DirNode ScanRoot(string rootPath, DirNode parent)
        {
            DirNode n = null;
            if (UseFast)
            {
                long f0 = Files, b0 = Bytes;
                int big0 = BigFiles.Count;
                var top0 = new List<FileEntry>(TopFiles);
                var ext0 = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, ExtStat> kv in Exts)
                    ext0[kv.Key] = new ExtStat { Ext = kv.Value.Ext, Size = kv.Value.Size, Count = kv.Value.Count };
                try
                {
                    string fmt = new DriveInfo(rootPath).DriveFormat;
                    if (string.Equals(fmt, "NTFS", StringComparison.OrdinalIgnoreCase))
                    {
                        n = new MftReader(this).Scan(rootPath);
                        FastDrives++;
                    }
                }
                catch
                {
                    // undo anything the fast scan added, then fall back
                    n = null;
                    Files = f0;
                    Bytes = b0;
                    BigFiles.RemoveRange(big0, BigFiles.Count - big0);
                    TopFiles = top0;
                    FindMin();
                    Exts = ext0;
                }
            }
            if (n == null)
            {
                if (Cancel) n = new DirNode { Name = rootPath, FullPath = rootPath };
                else
                {
                    n = ScanDir(new DirectoryInfo(rootPath), null);
                    SlowDrives++;
                }
            }
            n.Parent = parent;
            n.Name = rootPath.TrimEnd('\\');
            return n;
        }

        internal static int BySize(DirNode a, DirNode b) { return b.Size.CompareTo(a.Size); }

        DirNode ScanDir(DirectoryInfo di, DirNode parent)
        {
            var node = new DirNode { Name = di.Name, FullPath = di.FullName, Parent = parent };
            if (Cancel) return node;
            Current = di.FullName;

            try
            {
                foreach (FileInfo fi in di.EnumerateFiles())
                {
                    long len;
                    try { len = fi.Length; } catch { continue; }
                    RegisterFile(node, fi.Name, len, 0, fi);
                }
            }
            catch { Denied++; }

            try
            {
                foreach (DirectoryInfo sub in di.EnumerateDirectories())
                {
                    if (Cancel) break;
                    try
                    {
                        // skip shortcuts/junctions so nothing is counted twice
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    node.Children.Add(ScanDir(sub, node));
                }
            }
            catch { Denied++; }

            Totals(node, false);
            return node;
        }

        internal static void Totals(DirNode node, bool recurse)
        {
            if (recurse) foreach (DirNode c in node.Children) Totals(c, true);
            node.Size = node.OwnSize;
            node.FileCount = node.OwnCount;
            foreach (DirNode c in node.Children)
            {
                node.Size += c.Size;
                node.FileCount += c.FileCount;
            }
            node.Children.Sort(BySize);
        }

        internal static string ExtOf(string name)
        {
            int dot = name.LastIndexOf('.');
            return (dot >= 0 && dot < name.Length - 1) ? name.Substring(dot) : "";
        }

        // Counts one file. fi is given by the normal scan; the fast scan passes a FILETIME instead.
        internal void RegisterFile(DirNode node, string name, long len, long fileTime, FileInfo fi)
        {
            if (string.Equals(name, "smartplantv4.ini", StringComparison.OrdinalIgnoreCase) &&
                node != null && !string.IsNullOrEmpty(node.FullPath))
            {
                string ini = Path.Combine(node.FullPath, name);
                if (iniPathSet.Add(ini)) IniPaths.Add(ini);
            }
            node.OwnSize += len;
            node.OwnCount++;
            Files++;
            Bytes += len;

            string ext = ExtOf(name);
            if (ext.Length == 0) ext = "(no extension)";
            ExtStat es;
            if (!Exts.TryGetValue(ext, out es))
            {
                es = new ExtStat { Ext = ext.ToLowerInvariant() };
                Exts[ext] = es;
            }
            es.Size += len;
            es.Count++;

            bool big = len >= BigFileMin;
            bool top = TopFiles.Count < TopN || len > topMin;
            if (!big && !top) return;

            DateTime mod = DateTime.MinValue;
            try
            {
                if (fi != null) mod = fi.LastWriteTime;
                else if (fileTime > 0) mod = DateTime.FromFileTime(fileTime);
            }
            catch { }
            string full = fi != null ? fi.FullName : node.FullPath.TrimEnd('\\') + "\\" + name;
            var fe = new FileEntry { Name = name, FullPath = full, Ext = es.Ext, Size = len, Modified = mod, Folder = node };
            if (big) BigFiles.Add(fe);
            if (top) AddTopEntry(fe);
        }

        void AddTopEntry(FileEntry fe)
        {
            if (TopFiles.Count < TopN)
            {
                TopFiles.Add(fe);
                if (TopFiles.Count == TopN) FindMin();
                return;
            }
            TopFiles[topMinIdx] = fe;
            FindMin();
        }

        void FindMin()
        {
            topMin = long.MaxValue;
            topMinIdx = -1;
            for (int i = 0; i < TopFiles.Count; i++)
            {
                if (TopFiles[i].Size < topMin) { topMin = TopFiles[i].Size; topMinIdx = i; }
            }
            if (TopFiles.Count < TopN) topMin = long.MaxValue;
        }
    }

    // ------------------------------------------------------------------
    //  FAST SCAN: reads the NTFS Master File Table (the disk's index) directly
    // ------------------------------------------------------------------
    class MftReader
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize,
            [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, [Out] byte[] buffer, int toRead, out int read, IntPtr overlapped);

        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ_WRITE = 3;
        const uint OPEN_EXISTING = 3;
        const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
        const long REF_MASK = 0x0000FFFFFFFFFFFF;

        readonly Scanner sc;
        readonly Func<bool> cancelled;
        readonly Action<string> report;
        int recSize, bpc;
        int count;
        bool[] inUse, isDir;
        byte[] nameSpace;
        string[] names;
        int[] parents;
        long[] sizes, mtimes, ctimes;

        public MftReader(Scanner scanner)
        {
            sc = scanner;
            cancelled = () => scanner.Cancel;
            report = t => scanner.Current = t;
        }

        // Used by the Cleanup Advisor: only reads the index, no folder tree
        public MftReader(Func<bool> cancel, Action<string> status)
        {
            cancelled = cancel;
            report = status;
        }

        public DirNode Scan(string rootPath)
        {
            ReadRecords(rootPath);
            return Build(rootPath);
        }

        // Hands the raw index to MftIndex (Cleanup Advisor)
        public MftIndex ReadIndex(string rootPath)
        {
            ReadRecords(rootPath);
            return new MftIndex(rootPath, count, inUse, isDir, names, parents, sizes, mtimes, ctimes);
        }

        void ReadRecords(string rootPath)
        {
            string letter = rootPath.TrimEnd('\\');
            using (SafeFileHandle h = CreateFile(@"\\.\" + letter, GENERIC_READ, FILE_SHARE_READ_WRITE,
                       IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new IOException("Cannot open the volume.");

                byte[] vd = new byte[128];
                int ret;
                if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, vd, vd.Length, out ret, IntPtr.Zero))
                    throw new IOException("Not an NTFS volume.");

                int bps = BitConverter.ToInt32(vd, 40);
                bpc = BitConverter.ToInt32(vd, 44);
                recSize = BitConverter.ToInt32(vd, 48);
                long mftValid = BitConverter.ToInt64(vd, 56);
                long mftLcn = BitConverter.ToInt64(vd, 64);
                if (bps <= 0 || bpc <= 0 || recSize <= 0 || recSize > 65536 || bpc < recSize || bpc % recSize != 0)
                    throw new NotSupportedException("Unusual disk layout.");
                long total = mftValid / recSize;
                if (total <= 16 || total > int.MaxValue - 1) throw new NotSupportedException("Unexpected index size.");
                count = (int)total;

                // record 0 describes where the index itself is stored
                byte[] first = new byte[bpc];
                ReadInto(h, mftLcn * bpc, first, bpc);
                if (!Fixup(first, 0, recSize)) throw new IOException("Disk index is not readable.");
                List<long[]> runs = DataRuns(first, 0, recSize);
                if (runs.Count == 0) throw new IOException("Disk index layout not found.");

                inUse = new bool[count];
                isDir = new bool[count];
                nameSpace = new byte[count];
                names = new string[count];
                parents = new int[count];
                sizes = new long[count];
                mtimes = new long[count];
                ctimes = new long[count];

                int chunk = 4 * 1024 * 1024;
                byte[] buf = new byte[chunk];
                long rec = 0;
                foreach (long[] run in runs)
                {
                    long runBytes = run[1] * bpc;
                    long pos = run[0] * bpc;
                    long done = 0;
                    while (done < runBytes && rec < count)
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        int toRead = (int)Math.Min(chunk, runBytes - done);
                        ReadInto(h, pos + done, buf, toRead);
                        int recs = toRead / recSize;
                        for (int k = 0; k < recs && rec < count; k++, rec++)
                            ParseRecord(buf, k * recSize, (int)rec);
                        done += toRead;
                        report(string.Format("the disk index of {0} ({1}%)", letter, rec * 100 / count));
                    }
                }
            }
        }

        static void ReadInto(SafeFileHandle h, long pos, byte[] buf, int len)
        {
            long np;
            if (!SetFilePointerEx(h, pos, out np, 0)) throw new IOException("Seek failed.");
            int got;
            if (!ReadFile(h, buf, len, out got, IntPtr.Zero) || got != len) throw new IOException("Read failed.");
        }

        static bool Fixup(byte[] b, int ofs, int size)
        {
            if (BitConverter.ToUInt32(b, ofs) != 0x454C4946) return false; // "FILE"
            int usaOfs = BitConverter.ToUInt16(b, ofs + 4);
            int usaCount = BitConverter.ToUInt16(b, ofs + 6);
            if (usaCount < 2 || usaOfs + usaCount * 2 > size) return false;
            int stride = size / (usaCount - 1);
            ushort usn = BitConverter.ToUInt16(b, ofs + usaOfs);
            for (int i = 1; i < usaCount; i++)
            {
                int p = ofs + i * stride - 2;
                if (BitConverter.ToUInt16(b, p) != usn) return false;
                b[p] = b[ofs + usaOfs + i * 2];
                b[p + 1] = b[ofs + usaOfs + i * 2 + 1];
            }
            return true;
        }

        static List<long[]> DataRuns(byte[] b, int ofs, int size)
        {
            var runs = new List<long[]>();
            int a = ofs + BitConverter.ToUInt16(b, ofs + 0x14);
            int end = ofs + size;
            while (a + 16 <= end)
            {
                uint type = BitConverter.ToUInt32(b, a);
                if (type == 0xFFFFFFFF) break;
                int len = (int)BitConverter.ToUInt32(b, a + 4);
                if (len <= 0 || a + len > end) break;
                if (type == 0x20) throw new NotSupportedException("Very fragmented disk index.");
                if (type == 0x80 && b[a + 8] == 1 && b[a + 9] == 0)
                {
                    int rp = a + BitConverter.ToUInt16(b, a + 32);
                    long lcn = 0;
                    while (rp < a + len && b[rp] != 0)
                    {
                        int hdr = b[rp];
                        int lb = hdr & 0x0F, ob = hdr >> 4;
                        rp++;
                        if (lb == 0 || lb > 8 || ob > 8 || rp + lb + ob > a + len) break;
                        long length = 0;
                        for (int i = 0; i < lb; i++) length |= (long)b[rp + i] << (8 * i);
                        rp += lb;
                        if (ob == 0) throw new NotSupportedException("Sparse disk index.");
                        long off = 0;
                        for (int i = 0; i < ob; i++) off |= (long)b[rp + i] << (8 * i);
                        if (ob < 8 && (b[rp + ob - 1] & 0x80) != 0) off |= -1L << (8 * ob);
                        rp += ob;
                        lcn += off;
                        runs.Add(new long[] { lcn, length });
                    }
                }
                a += len;
            }
            return runs;
        }

        void ParseRecord(byte[] b, int ofs, int index)
        {
            if (!Fixup(b, ofs, recSize)) return;
            int flags = BitConverter.ToUInt16(b, ofs + 0x16);
            if ((flags & 1) == 0) return; // not in use
            long baseRef = BitConverter.ToInt64(b, ofs + 0x20) & REF_MASK;
            long target = baseRef != 0 ? baseRef : index;
            if (target < 0 || target >= count) return;
            int t = (int)target;
            if (baseRef == 0)
            {
                inUse[t] = true;
                isDir[t] = (flags & 2) != 0;
            }

            int a = ofs + BitConverter.ToUInt16(b, ofs + 0x14);
            int end = ofs + recSize;
            while (a + 16 <= end)
            {
                uint type = BitConverter.ToUInt32(b, a);
                if (type == 0xFFFFFFFF) break;
                int len = (int)BitConverter.ToUInt32(b, a + 4);
                if (len <= 0 || a + len > end) break;
                byte nonRes = b[a + 8];
                byte attrNameLen = b[a + 9];

                if (type == 0x10 && nonRes == 0)
                {
                    int c = a + BitConverter.ToUInt16(b, a + 20);
                    if (c + 16 <= a + len)
                    {
                        ctimes[t] = BitConverter.ToInt64(b, c);
                        mtimes[t] = BitConverter.ToInt64(b, c + 8);
                    }
                }
                else if (type == 0x30 && nonRes == 0)
                {
                    int c = a + BitConverter.ToUInt16(b, a + 20);
                    if (c + 66 <= a + len)
                    {
                        int nlen = b[c + 64];
                        byte ns = b[c + 65];
                        if (c + 66 + nlen * 2 <= a + len && (names[t] == null || (nameSpace[t] == 2 && ns != 2)))
                        {
                            names[t] = Encoding.Unicode.GetString(b, c + 66, nlen * 2);
                            parents[t] = (int)(BitConverter.ToInt64(b, c) & REF_MASK);
                            nameSpace[t] = ns;
                        }
                    }
                }
                else if (type == 0x80 && attrNameLen == 0)
                {
                    if (nonRes == 0) sizes[t] = BitConverter.ToUInt32(b, a + 16);
                    else if (BitConverter.ToInt64(b, a + 16) == 0) sizes[t] = BitConverter.ToInt64(b, a + 48);
                }
                a += len;
            }
        }

        DirNode Build(string rootPath)
        {
            const int ROOT = 5;
            report("the folder list of " + rootPath.TrimEnd('\\'));
            var dirs = new DirNode[count];
            for (int i = 0; i < count; i++)
            {
                if (!inUse[i] || !isDir[i] || names[i] == null) continue;
                if (i != ROOT && i < 16) continue; // skip NTFS system records
                dirs[i] = new DirNode { Name = names[i] };
            }
            if (dirs[ROOT] == null) throw new IOException("Root folder not found.");
            DirNode root = dirs[ROOT];

            for (int i = 0; i < count; i++)
            {
                if (dirs[i] == null || i == ROOT) continue;
                int p = parents[i];
                if (p < 0 || p >= count || p == i || dirs[p] == null) continue;
                dirs[i].Parent = dirs[p];
                dirs[p].Children.Add(dirs[i]);
            }

            root.FullPath = rootPath.TrimEnd('\\') + "\\";
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DirNode d = stack.Pop();
                string basePath = d.FullPath.TrimEnd('\\');
                foreach (DirNode c in d.Children)
                {
                    c.FullPath = basePath + "\\" + c.Name;
                    stack.Push(c);
                }
            }

            for (int i = 16; i < count; i++)
            {
                if ((i & 0xFFFF) == 0 && cancelled()) throw new OperationCanceledException();
                if (!inUse[i] || isDir[i] || names[i] == null) continue;
                int p = parents[i];
                if (p < 0 || p >= count) continue;
                DirNode d = dirs[p];
                if (d == null || d.FullPath == null) continue;
                sc.RegisterFile(d, names[i], sizes[i], mtimes[i], null);
            }

            Scanner.Totals(root, true);
            return root;
        }
    }


    // ------------------------------------------------------------------
    //  The disk index in memory, so the Cleanup Advisor can look inside
    //  folders without opening them one by one
    // ------------------------------------------------------------------
    class MftIndex
    {
        const int ROOT = 5;
        readonly string root;
        readonly int count;
        readonly bool[] inUse, isDir;
        readonly string[] names;
        readonly int[] parents;
        readonly long[] sizes, mtimes, ctimes;
        int[] childStart, childList;

        public MftIndex(string rootPath, int n, bool[] used, bool[] dir, string[] nm, int[] par,
                        long[] sz, long[] mt, long[] ct)
        {
            root = rootPath.TrimEnd('\\');
            count = n; inUse = used; isDir = dir; names = nm; parents = par;
            sizes = sz; mtimes = mt; ctimes = ct;
            BuildChildren();
        }

        bool Listed(int i)
        {
            if (i < 16 || !inUse[i] || names[i] == null) return false;
            int p = parents[i];
            return p >= 0 && p < count && p != i;
        }

        // Compact "who is inside which folder" table
        void BuildChildren()
        {
            childStart = new int[count + 1];
            for (int i = 16; i < count; i++)
                if (Listed(i)) childStart[parents[i] + 1]++;
            for (int i = 0; i < count; i++) childStart[i + 1] += childStart[i];
            childList = new int[childStart[count]];
            var fill = new int[count];
            Array.Copy(childStart, fill, count);
            for (int i = 16; i < count; i++)
                if (Listed(i)) childList[fill[parents[i]]++] = i;
        }

        // Finds the record of a path like C:\Users\bob\AppData\Local\Temp, or -1
        public int Find(string path)
        {
            string p = path.TrimEnd('\\');
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return -1;
            string rest = p.Substring(root.Length).Trim('\\');
            int cur = ROOT;
            if (rest.Length == 0) return cur;
            foreach (string part in rest.Split('\\'))
            {
                int next = -1;
                for (int k = childStart[cur]; k < childStart[cur + 1]; k++)
                {
                    int c = childList[k];
                    if (string.Equals(names[c], part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                }
                if (next < 0) return -1;
                cur = next;
            }
            return cur;
        }

        public bool IsDir(int rec) { return isDir[rec]; }

        static DateTime Time(long ft)
        {
            try { return ft > 0 ? DateTime.FromFileTime(ft) : DateTime.MinValue; } catch { return DateTime.MinValue; }
        }

        // Calls onFile(fullPath, name, size, modified, created) for every file at or below rec
        public void ForEachFile(int rec, string path, Func<bool> cancel,
                                Action<string, string, long, DateTime, DateTime> onFile)
        {
            if (!isDir[rec])
            {
                onFile(path, names[rec], sizes[rec], Time(mtimes[rec]), Time(ctimes[rec]));
                return;
            }
            var stack = new Stack<KeyValuePair<int, string>>();
            stack.Push(new KeyValuePair<int, string>(rec, path.TrimEnd('\\')));
            while (stack.Count > 0)
            {
                if (cancel()) return;
                KeyValuePair<int, string> d = stack.Pop();
                for (int k = childStart[d.Key]; k < childStart[d.Key + 1]; k++)
                {
                    int c = childList[k];
                    string full = d.Value + "\\" + names[c];
                    if (isDir[c]) stack.Push(new KeyValuePair<int, string>(c, full));
                    else onFile(full, names[c], sizes[c], Time(mtimes[c]), Time(ctimes[c]));
                }
            }
        }
    }

    // ------------------------------------------------------------------
    //  Search / filter box logic
    //  Examples: ".bak"   ".iso .zip"   "older than 1 year"   ">1gb"   "setup"
    // ------------------------------------------------------------------
    class FilterSpec
    {
        public List<string> Exts = new List<string>();
        public List<string> Words = new List<string>();
        public long MinSize = -1, MaxSize = -1;
        public DateTime OlderThan = DateTime.MinValue, NewerThan = DateTime.MinValue;

        public bool IsEmpty
        {
            get
            {
                return Exts.Count == 0 && Words.Count == 0 && MinSize < 0 && MaxSize < 0 &&
                       OlderThan == DateTime.MinValue && NewerThan == DateTime.MinValue;
            }
        }

        static readonly Regex AgeRx = new Regex(
            @"\b(older|newer)\s+than\s+(\d+)\s*(years?|months?|weeks?|days?|y|m|w|d)\b", RegexOptions.IgnoreCase);
        static readonly Regex SizeWordRx = new Regex(
            @"\b(bigger|larger|smaller|over|under)(?:\s+than)?\s+(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)\b", RegexOptions.IgnoreCase);
        static readonly Regex SizeSymRx = new Regex(
            @"(>=|<=|>|<)\s*(\d+(?:\.\d+)?)(?:\s*(kb|mb|gb|tb|b)\b)?", RegexOptions.IgnoreCase);

        static long Bytes(string num, string unit)
        {
            double v = double.Parse(num, CultureInfo.InvariantCulture);
            switch ((unit ?? "").ToLowerInvariant())
            {
                case "b": return (long)v;
                case "kb": return (long)(v * 1024);
                case "gb": return (long)(v * 1073741824.0);
                case "tb": return (long)(v * 1099511627776.0);
                default: return (long)(v * 1048576.0); // MB when no unit is given
            }
        }

        public static FilterSpec Parse(string text)
        {
            var f = new FilterSpec();
            string s = " " + (text ?? "").Trim().ToLowerInvariant() + " ";

            foreach (Match m in AgeRx.Matches(s))
            {
                int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string u = m.Groups[3].Value;
                DateTime t;
                if (u.StartsWith("y")) t = DateTime.Now.AddYears(-n);
                else if (u.StartsWith("mo") || u == "m") t = DateTime.Now.AddMonths(-n);
                else if (u.StartsWith("w")) t = DateTime.Now.AddDays(-7 * n);
                else t = DateTime.Now.AddDays(-n);
                if (m.Groups[1].Value == "older") f.OlderThan = t; else f.NewerThan = t;
            }
            s = AgeRx.Replace(s, " ");

            foreach (Match m in SizeWordRx.Matches(s))
            {
                long v = Bytes(m.Groups[2].Value, m.Groups[3].Value);
                string w = m.Groups[1].Value;
                if (w == "smaller" || w == "under") f.MaxSize = v; else f.MinSize = v;
            }
            s = SizeWordRx.Replace(s, " ");

            foreach (Match m in SizeSymRx.Matches(s))
            {
                long v = Bytes(m.Groups[2].Value, m.Groups[3].Value);
                if (m.Groups[1].Value.StartsWith("<")) f.MaxSize = v; else f.MinSize = v;
            }
            s = SizeSymRx.Replace(s, " ");

            foreach (string raw in s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string tok = raw.Trim('"', '\'');
                if (tok.StartsWith("*.")) tok = tok.Substring(1);
                if (tok.Length > 1 && tok[0] == '.' && tok.IndexOf('.', 1) < 0) f.Exts.Add(tok);
                else if (tok.Length > 0) f.Words.Add(tok);
            }
            return f;
        }

        public bool MatchFile(FileEntry fe)
        {
            return Match(fe.FullPath, fe.Ext, fe.Size, fe.Modified);
        }

        public bool MatchHit(FileHit h)
        {
            string ext = Scanner.ExtOf(h.Name).ToLowerInvariant();
            if (ext.Length == 0) ext = "(no extension)";
            return Match(h.Path, ext, h.Size, h.Modified);
        }

        public bool Match(string fullPath, string ext, long size, DateTime modified)
        {
            if (Exts.Count > 0 && !Exts.Contains(ext)) return false;
            if (MinSize >= 0 && size < MinSize) return false;
            if (MaxSize >= 0 && size > MaxSize) return false;
            if (OlderThan != DateTime.MinValue && (modified == DateTime.MinValue || modified >= OlderThan)) return false;
            if (NewerThan != DateTime.MinValue && (modified == DateTime.MinValue || modified < NewerThan)) return false;
            foreach (string w in Words)
                if (fullPath.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        // Folders only have a name and a size
        public bool MatchFolder(string name, long size)
        {
            if (MinSize >= 0 && size < MinSize) return false;
            if (MaxSize >= 0 && size > MaxSize) return false;
            foreach (string w in Words)
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        public bool MatchType(ExtStat x)
        {
            if (Exts.Count > 0 && !Exts.Contains(x.Ext)) return false;
            foreach (string w in Words)
                if (x.Ext.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }
    }

    // ------------------------------------------------------------------
    //  Duplicate finder: same size, then same content (SHA-1 of the whole file)
    // ------------------------------------------------------------------
    class DupGroup
    {
        public long Size;
        public List<FileEntry> Files = new List<FileEntry>();
        public Color Color;
        public long Wasted { get { return Size * Math.Max(0, Files.Count - 1); } }
    }

    class DupFinder
    {
        public volatile bool Cancel;
        public long BytesToCheck, BytesChecked;
        public volatile string Stage = "";

        public List<DupGroup> Find(List<FileEntry> files)
        {
            var result = new List<DupGroup>();
            List<List<FileEntry>> bySize = files
                .Where(f => f.Size > 0 && !Util.IsProtected(f.FullPath))
                .GroupBy(f => f.Size)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList()).ToList();

            // Step 1: quick check of the first and last 64 KB
            Stage = "Quick check";
            var candidates = new List<List<FileEntry>>();
            foreach (List<FileEntry> group in bySize)
            {
                if (Cancel) return result;
                var bySample = new Dictionary<string, List<FileEntry>>();
                foreach (FileEntry f in group)
                {
                    string h = SampleHash(f.FullPath, f.Size);
                    if (h == null) continue;
                    List<FileEntry> list;
                    if (!bySample.TryGetValue(h, out list)) { list = new List<FileEntry>(); bySample[h] = list; }
                    list.Add(f);
                }
                foreach (List<FileEntry> l in bySample.Values)
                    if (l.Count > 1) candidates.Add(l);
            }

            // Step 2: full content check
            BytesToCheck = candidates.Sum(l => l[0].Size * l.Count);
            Stage = "Comparing file contents";
            foreach (List<FileEntry> group in candidates)
            {
                if (Cancel) return result;
                var byHash = new Dictionary<string, List<FileEntry>>();
                foreach (FileEntry f in group)
                {
                    string h = FullHash(f.FullPath);
                    if (h == null) continue;
                    List<FileEntry> list;
                    if (!byHash.TryGetValue(h, out list)) { list = new List<FileEntry>(); byHash[h] = list; }
                    list.Add(f);
                }
                foreach (List<FileEntry> l in byHash.Values)
                    if (l.Count > 1) result.Add(new DupGroup { Size = l[0].Size, Files = l });
            }
            return result;
        }

        static string SampleHash(string path, long size)
        {
            try
            {
                using (var sha = new SHA1CryptoServiceProvider())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                {
                    int n = (int)Math.Min(65536, size);
                    byte[] buf = new byte[n];
                    int got = fs.Read(buf, 0, n);
                    sha.TransformBlock(buf, 0, got, null, 0);
                    if (size > 65536)
                    {
                        fs.Seek(-n, SeekOrigin.End);
                        got = fs.Read(buf, 0, n);
                        sha.TransformBlock(buf, 0, got, null, 0);
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    return BitConverter.ToString(sha.Hash);
                }
            }
            catch { return null; }
        }

        string FullHash(string path)
        {
            try
            {
                using (var sha = new SHA1CryptoServiceProvider())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                {
                    byte[] buf = new byte[1 << 20];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (Cancel) return null;
                        sha.TransformBlock(buf, 0, n, null, 0);
                        BytesChecked += n;
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    return BitConverter.ToString(sha.Hash);
                }
            }
            catch { return null; }
        }
    }

    // Rounded search box with a placeholder
    class SearchBox : Panel
    {
        public TextBox Box;
        bool focused;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public SearchBox(string placeholder)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            Height = 34;
            Padding = new Padding(12, 9, 10, 4);
            Box = new TextBox { BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = Theme.Body, BackColor = Theme.Surface, ForeColor = Theme.Ink };
            Box.HandleCreated += (s, e) => SendMessage(Box.Handle, 0x1501, (IntPtr)1, placeholder);
            Box.GotFocus += (s, e) => { focused = true; Invalidate(); };
            Box.LostFocus += (s, e) => { focused = false; Invalidate(); };
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
            Click += (s, e) => Box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Util.RoundRect(r, 8))
            using (var b = new SolidBrush(Theme.Surface))
            using (var p = new Pen(focused ? Theme.Accent : Theme.Border, focused ? 1.5f : 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }
    }

    // ------------------------------------------------------------------
    //  Custom-drawn controls
    // ------------------------------------------------------------------
    class Canvas : Control
    {
        protected ToolTip Tip = new ToolTip();

        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            BackColor = Theme.Surface;
            Font = Theme.Body;
        }

        protected static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        protected void DrawEmpty(Graphics g, string text)
        {
            TextRenderer.DrawText(g, text, Theme.Body, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }

    enum BtnKind { Primary, Secondary, Danger, Tab }

    class FlatBtn : Canvas
    {
        public BtnKind Kind;
        bool active, hover, down;

        public bool Active
        {
            get { return active; }
            set { active = value; Invalidate(); }
        }

        public FlatBtn(string text, BtnKind kind)
        {
            Text = text;
            Kind = kind;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            Height = 34;
            Width = TextRenderer.MeasureText(text, Theme.BodyBold).Width + 32;
            Margin = new Padding(8, 0, 0, 0);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, border, text;
            switch (Kind)
            {
                case BtnKind.Primary:
                    fill = !Enabled ? Theme.Lighten(Theme.Accent, 0.55f)
                         : down ? Theme.Darken(Theme.Accent, 0.18f)
                         : hover ? Theme.Darken(Theme.Accent, 0.1f) : Theme.Accent;
                    border = fill; text = Color.White;
                    break;
                case BtnKind.Danger:
                    fill = hover && Enabled ? Color.FromArgb(253, 240, 240) : Theme.Surface;
                    border = Enabled ? Color.FromArgb(240, 196, 196) : Theme.Border;
                    text = Enabled ? Theme.Danger : Theme.Faint;
                    break;
                case BtnKind.Tab:
                    fill = active ? Theme.Ink : (hover ? Color.FromArgb(226, 231, 238) : Theme.Bg);
                    border = fill;
                    text = active ? Color.White : Theme.Muted;
                    break;
                default:
                    fill = hover && Enabled ? Theme.SurfaceAlt : Theme.Surface;
                    border = Theme.Border;
                    text = Enabled ? Theme.Ink : Theme.Faint;
                    break;
            }
            using (GraphicsPath path = Util.RoundRect(r, 8))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Theme.BodyBold, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // White rounded panel with a title
    class Card : Panel
    {
        string title = "", subtitle = "";
        public string Title { get { return title; } set { title = value; Invalidate(); } }
        public string Subtitle { get { return subtitle; } set { subtitle = value; Invalidate(); } }

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            Padding = new Padding(14, 46, 14, 14);
            Dock = DockStyle.Fill;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Util.RoundRect(r, 10))
            using (var b = new SolidBrush(Theme.Surface))
            using (var p = new Pen(Theme.Border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, title, Theme.BodyBold, new Point(16, 15), Theme.Ink, TextFormatFlags.NoPrefix);
            if (subtitle.Length > 0)
            {
                int tw = TextRenderer.MeasureText(title, Theme.BodyBold).Width;
                TextRenderer.DrawText(g, subtitle, Theme.Small,
                    new Rectangle(16 + tw + 6, 16, Math.Max(10, Width - tw - 40), 20), Theme.Muted,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    // A clickable drive tile with a usage bar
    class DriveCard : Canvas
    {
        public DriveInfo Drive;
        public long Total, Free;
        public string VolLabel;
        bool selected, hover;

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public DriveCard(DriveInfo d)
        {
            Drive = d;
            Size = new Size(236, 96);
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, 12, 0);
            BackColor = Theme.Bg;
            try { VolLabel = d.VolumeLabel; } catch { VolLabel = ""; }
            if (string.IsNullOrEmpty(VolLabel)) VolLabel = "Local Disk";
            Reload();
        }

        public void Reload()
        {
            try { Total = Drive.TotalSize; Free = Drive.TotalFreeSpace; } catch { }
            Invalidate();
        }

        public double UsedPct { get { return Total > 0 ? (Total - Free) * 100.0 / Total : 0; } }

        protected override void OnClick(EventArgs e) { Selected = !selected; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Bg);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            Color fill = selected ? Theme.AccentSoft : Theme.Surface;
            Color border = selected ? Theme.Accent : (hover && Enabled ? Theme.Faint : Theme.Border);
            using (GraphicsPath path = Util.RoundRect(r, 10))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(border, selected ? 2f : 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }

            string letter = Drive.Name.TrimEnd('\\');
            Color ink = Enabled ? Theme.Ink : Theme.Faint;
            TextRenderer.DrawText(g, letter, Theme.DriveFont, new Point(12, 8), ink, TextFormatFlags.NoPrefix);
            int lw = TextRenderer.MeasureText(letter, Theme.DriveFont).Width;
            TextRenderer.DrawText(g, VolLabel, Theme.Small, new Rectangle(10 + lw, 13, Width - lw - 56, 22), Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // checkbox
            var cb = new RectangleF(Width - 34, 14, 18, 18);
            using (GraphicsPath path = Util.RoundRect(cb, 5))
            {
                if (selected)
                {
                    using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                    using (var p = new Pen(Color.White, 2f))
                        g.DrawLines(p, new[] {
                            new PointF(cb.X + 4, cb.Y + 9.5f), new PointF(cb.X + 7.5f, cb.Y + 13), new PointF(cb.X + 14, cb.Y + 5.5f) });
                }
                else
                {
                    using (var p = new Pen(Theme.Faint, 1.5f)) g.DrawPath(p, path);
                }
            }

            double pct = UsedPct;
            Color usage = Theme.UsageColor(pct);
            var track = new RectangleF(14, 52, Width - 28, 8);
            using (GraphicsPath path = Util.RoundRect(track, 4))
            using (var b = new SolidBrush(Theme.Track))
                g.FillPath(b, path);
            var bar = new RectangleF(track.X, track.Y, (float)(track.Width * pct / 100.0), track.Height);
            if (bar.Width >= 1)
            {
                using (GraphicsPath path = Util.RoundRect(bar, 4))
                using (var b = new SolidBrush(usage))
                    g.FillPath(b, path);
            }

            TextRenderer.DrawText(g, Util.Size(Free) + " free of " + Util.Size(Total), Theme.Small,
                new Point(12, 67), Theme.Muted, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, pct.ToString("0") + "% used", Theme.SmallBold,
                new Rectangle(0, 67, Width - 14, 18), usage == Theme.Accent ? Theme.Ink : usage,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }
    }

    // ---------- Treemap (boxes sized by space used) ----------
    class MapItem
    {
        public string Label;
        public long Size;
        public object Tag;
        public Color Color;
        public bool Clickable;
        public RectangleF Rect;
    }

    class Treemap : Canvas
    {
        List<MapItem> items = new List<MapItem>();
        MapItem hover;
        double total;
        public string EmptyText = "Choose a drive above and click Scan drives.";
        public event Action<MapItem> ItemClicked;

        public void SetItems(List<MapItem> list)
        {
            items = list;
            hover = null;
            DoLayout();
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        void DoLayout()
        {
            foreach (MapItem it in items) it.Rect = RectangleF.Empty;
            List<MapItem> list = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
            total = 0;
            foreach (MapItem it in list) total += it.Size;
            var r = new RectangleF(0, 0, Width, Height);
            if (list.Count == 0 || r.Width < 4 || r.Height < 4 || total <= 0) return;

            double scale = r.Width * (double)r.Height / total;
            double[] areas = list.Select(i => i.Size * scale).ToArray();
            int n = list.Count, i0 = 0;

            // "Squarified" treemap layout: keeps boxes as square as possible
            while (i0 < n)
            {
                double side = Math.Min(r.Width, r.Height);
                if (side <= 0.5) break;
                int j = i0;
                double sum = 0, prevWorst = double.MaxValue;
                while (j < n)
                {
                    double s = sum + areas[j];
                    double w = Worst(areas, i0, j, s, side);
                    if (j > i0 && w > prevWorst) break;
                    prevWorst = w;
                    sum = s;
                    j++;
                }

                if (r.Width >= r.Height)
                {
                    float colW = (float)(sum / r.Height);
                    float y = r.Y;
                    for (int k = i0; k < j; k++)
                    {
                        float h = (float)(areas[k] / colW);
                        list[k].Rect = new RectangleF(r.X, y, colW, h);
                        y += h;
                    }
                    r = new RectangleF(r.X + colW, r.Y, r.Width - colW, r.Height);
                }
                else
                {
                    float rowH = (float)(sum / r.Width);
                    float x = r.X;
                    for (int k = i0; k < j; k++)
                    {
                        float w = (float)(areas[k] / rowH);
                        list[k].Rect = new RectangleF(x, r.Y, w, rowH);
                        x += w;
                    }
                    r = new RectangleF(r.X, r.Y + rowH, r.Width, r.Height - rowH);
                }
                i0 = j;
            }
        }

        static double Worst(double[] a, int from, int to, double sum, double side)
        {
            double mx = 0, mn = double.MaxValue;
            for (int k = from; k <= to; k++)
            {
                if (a[k] > mx) mx = a[k];
                if (a[k] < mn) mn = a[k];
            }
            double s2 = sum * sum, w2 = side * side;
            return Math.Max(w2 * mx / s2, s2 / (w2 * mn));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Hq(g);
            g.Clear(BackColor);
            if (items.Count == 0 || total <= 0) { DrawEmpty(g, EmptyText); return; }

            foreach (MapItem it in items)
            {
                RectangleF rr = RectangleF.Inflate(it.Rect, -1.5f, -1.5f);
                if (rr.Width < 1 || rr.Height < 1) continue;
                Color c = it == hover ? Theme.Lighten(it.Color, 0.22f) : it.Color;
                float rad = Math.Min(6f, Math.Min(rr.Width, rr.Height) / 3f);
                using (GraphicsPath path = Util.RoundRect(rr, rad))
                using (var b = new SolidBrush(c))
                    g.FillPath(b, path);

                if (rr.Width > 56 && rr.Height > 30)
                {
                    var tr = Rectangle.Round(new RectangleF(rr.X + 8, rr.Y + 6, rr.Width - 14, 20));
                    TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis |
                                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                    TextRenderer.DrawText(g, it.Label, Theme.BodyBold, tr, Color.White, f);
                    if (rr.Height > 50)
                        TextRenderer.DrawText(g, Util.Size(it.Size), Theme.Small,
                            new Rectangle(tr.X, tr.Y + 20, tr.Width, 18), Theme.Lighten(it.Color, 0.75f), f);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            MapItem hit = null;
            foreach (MapItem it in items)
                if (it.Rect.Contains(e.Location)) { hit = it; break; }
            if (hit == hover) return;
            hover = hit;
            Invalidate();
            if (hit != null)
            {
                double pct = total > 0 ? hit.Size * 100.0 / total : 0;
                Tip.SetToolTip(this, hit.Label + "\n" + Util.Size(hit.Size) + "  (" + pct.ToString("0.0") + "% of this folder)" +
                                     (hit.Clickable ? "\nClick to open" : ""));
                Cursor = hit.Clickable ? Cursors.Hand : Cursors.Default;
            }
            else
            {
                Tip.SetToolTip(this, "");
                Cursor = Cursors.Default;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = null;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && hover != null && ItemClicked != null) ItemClicked(hover);
        }
    }

    // ---------- Donut chart ----------
    class Seg
    {
        public string Label, Detail;
        public long Value;
        public Color Color;
    }

    class Donut : Canvas
    {
        List<Seg> segs = new List<Seg>();
        string center = "", centerSub = "";
        public string EmptyText = "Scan a drive to see space by file type.";

        public void SetData(List<Seg> s, string c, string sub)
        {
            segs = s; center = c; centerSub = sub;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Hq(g);
            g.Clear(BackColor);
            long total = 0;
            foreach (Seg s in segs) total += Math.Max(0, s.Value);
            if (total <= 0) { DrawEmpty(g, EmptyText); return; }

            bool legend = Width >= 380;
            int legendW = legend ? Math.Min(250, Width / 2) : 0;
            float size = Math.Min(Width - legendW - 24, Height - 24);
            if (size < 60) size = Math.Max(20, Math.Min(Width, Height) - 10);
            float cx = legend ? 8 + (Width - legendW - 16 - size) / 2f : (Width - size) / 2f;
            float cy = (Height - size) / 2f;
            float thick = size * 0.16f;
            var arcRect = new RectangleF(cx + thick / 2, cy + thick / 2, size - thick, size - thick);

            float start = -90f;
            foreach (Seg s in segs)
            {
                float sweep = (float)(360.0 * s.Value / total);
                if (sweep <= 0) continue;
                float drawn = segs.Count > 1 ? Math.Max(0.5f, sweep - 1.2f) : 359.9f;
                using (var p = new Pen(s.Color, thick)) g.DrawArc(p, arcRect, start, drawn);
                start += sweep;
            }

            TextRenderer.DrawText(g, center, Theme.Big, new Rectangle((int)cx, (int)(cy + size / 2 - 26), (int)size, 28),
                Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, centerSub, Theme.Small, new Rectangle((int)cx, (int)(cy + size / 2 + 2), (int)size, 20),
                Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);

            if (!legend) return;
            int lx = Width - legendW, rowH = 26;
            int rows = Math.Min(segs.Count, Math.Max(1, (Height - 16) / rowH));
            int ly = (Height - rows * rowH) / 2;
            for (int i = 0; i < rows; i++)
            {
                Seg s = segs[i];
                int y = ly + i * rowH;
                using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, lx, y + 8, 10, 10);
                double pct = s.Value * 100.0 / total;
                TextRenderer.DrawText(g, s.Label, Theme.Body, new Rectangle(lx + 18, y, legendW - 18 - 116, rowH), Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Util.Size(s.Value) + "   " + pct.ToString("0") + "%", Theme.Small,
                    new Rectangle(lx + legendW - 118, y, 110, rowH), Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    // ---------- Horizontal bar chart ----------
    class Bars : Canvas
    {
        List<Seg> items = new List<Seg>();
        int hoverIdx = -1;
        public string EmptyText = "Scan a drive to list the largest files.";

        public void SetData(List<Seg> list) { items = list; hoverIdx = -1; Invalidate(); }

        float RowH { get { return items.Count == 0 ? 30 : Math.Min(34f, (Height - 8) / (float)items.Count); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Hq(g);
            g.Clear(BackColor);
            if (items.Count == 0) { DrawEmpty(g, EmptyText); return; }

            long max = Math.Max(1, items.Max(i => i.Value));
            float rowH = RowH;
            int labelW = (int)Math.Min(210, Width * 0.42f);
            int valueW = 76;
            float barMax = Width - labelW - valueW - 24;

            for (int i = 0; i < items.Count; i++)
            {
                Seg it = items[i];
                float y = 4 + i * rowH;
                if (i == hoverIdx)
                    using (var hb = new SolidBrush(Theme.SurfaceAlt)) g.FillRectangle(hb, 0, y, Width, rowH);
                TextRenderer.DrawText(g, it.Label, Theme.Body, new Rectangle(6, (int)y, labelW - 8, (int)rowH), Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                float bh = Math.Min(14f, rowH * 0.5f);
                float bw = Math.Max(3f, (float)(barMax * it.Value / (double)max));
                var br = new RectangleF(labelW + 6, y + (rowH - bh) / 2, bw, bh);
                if (barMax > 10)
                {
                    using (GraphicsPath path = Util.RoundRect(br, bh / 2))
                    using (var b = new SolidBrush(it.Color))
                        g.FillPath(b, path);
                }
                TextRenderer.DrawText(g, Util.Size(it.Value), Theme.Small,
                    new Rectangle((int)(br.Right + 8), (int)y, valueW, (int)rowH), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = items.Count == 0 ? -1 : (int)((e.Y - 4) / RowH);
            if (idx < 0 || idx >= items.Count) idx = -1;
            if (idx == hoverIdx) return;
            hoverIdx = idx;
            Invalidate();
            Tip.SetToolTip(this, idx >= 0 ? items[idx].Detail + "\n" + Util.Size(items[idx].Value) : "");
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIdx = -1; Invalidate(); }
    }

    // ------------------------------------------------------------------
    //  Table styling
    // ------------------------------------------------------------------
    static class GridStyle
    {
        public static DataGridView Make()
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill;
            g.BackgroundColor = Theme.Surface;
            g.BorderStyle = BorderStyle.None;
            g.CellBorderStyle = DataGridViewCellBorderStyle.None;   // we draw our own row line
            g.GridColor = Color.FromArgb(237, 240, 244);
            Color lineColor = Color.FromArgb(232, 236, 241);
            g.RowPostPaint += (s, e) =>
            {
                using (var pen = new Pen(lineColor))
                    e.Graphics.DrawLine(pen, e.RowBounds.Left, e.RowBounds.Bottom - 1, e.RowBounds.Right, e.RowBounds.Bottom - 1);
            };
            // A single-cell hover repaint would leave the row line half-drawn; repaint the
            // whole row instead so the separator is always redrawn cleanly.
            g.CellMouseEnter += (s, e) => { if (e.RowIndex >= 0) g.InvalidateRow(e.RowIndex); };
            g.CellMouseLeave += (s, e) => { if (e.RowIndex >= 0) g.InvalidateRow(e.RowIndex); };
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = true;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 34;
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted;
            g.ColumnHeadersDefaultCellStyle.Font = Theme.SmallBold;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            g.DefaultCellStyle.BackColor = Theme.Surface;
            g.DefaultCellStyle.ForeColor = Theme.Ink;
            g.DefaultCellStyle.Font = Theme.Body;
            g.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft;
            g.DefaultCellStyle.SelectionForeColor = Theme.Ink;
            g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            g.RowTemplate.Height = 32;
            DoubleBuffer(g);
            try
            {
                typeof(DataGridView).InvokeMember("DoubleBuffered",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty, null, g, new object[] { true });
            }
            catch { }
            return g;
        }

        public static void DoubleBuffer(DataGridView g)
        {
            try { typeof(DataGridView).InvokeMember("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty, null, g, new object[] { true }); }
            catch { }
        }

        public static DataGridViewTextBoxColumn Col(string name, string header, int width, float fillWeight, bool right)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name;
            c.HeaderText = header;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            if (fillWeight > 0)
            {
                c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                c.FillWeight = fillWeight;
                c.MinimumWidth = 80;
            }
            else c.Width = width;
            if (right)
            {
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            return c;
        }

        public static void PaintShare(DataGridViewCellPaintingEventArgs e)
        {
            // Let the grid paint the background AND the cell borders, so row lines never flicker.
            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);
            double pct = e.Value is double ? (double)e.Value : 0;
            Rectangle r = e.CellBounds;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(r.X + 8, r.Y + r.Height / 2f - 4, r.Width - 66, 8);
            if (track.Width > 10)
            {
                using (GraphicsPath path = Util.RoundRect(track, 4))
                using (var b = new SolidBrush(Theme.Track))
                    g.FillPath(b, path);
                float w = (float)(track.Width * Math.Min(100, pct) / 100.0);
                if (w >= 1)
                {
                    using (GraphicsPath path = Util.RoundRect(new RectangleF(track.X, track.Y, w, track.Height), 4))
                    using (var b = new SolidBrush(Theme.Accent))
                        g.FillPath(b, path);
                }
            }
            TextRenderer.DrawText(g, pct.ToString("0.0") + "%", Theme.Small, new Rectangle(r.Right - 56, r.Y, 50, r.Height),
                Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------
    //  CLEANUP ADVISOR - known places on Windows VMs that are safe to clean
    // ------------------------------------------------------------------
    enum Safety { Safe, Review, LeaveAlone }
    enum RuleKind { Files, ScanBackups, Hibernation, ComponentCleanup, Info }

    class FileHit
    {
        public string Path;
        public long Size;
        public DateTime Modified;
        public bool Selected = true;   // ticked for deletion

        public string Name { get { int i = Path.LastIndexOf('\\'); return i >= 0 ? Path.Substring(i + 1) : Path; } }
        public string Folder { get { int i = Path.LastIndexOf('\\'); return i > 0 ? Path.Substring(0, i) : ""; } }
    }

    class CleanupRule
    {
        public string Title, Description, OpenPath;
        public Safety Level;
        public RuleKind Kind = RuleKind.Files;
        public Func<List<string>> Targets;
        public string Pattern = "*";
        public int MinAgeDays;
        public bool RemoveEmptyFolders;
        public HashSet<string> Exclude;
        public Color Color;

        // results
        public List<FileHit> Hits = new List<FileHit>();
        public long Size;
        public volatile bool Analyzed;
        public bool Checked;
        public long Freed;
        public int Deleted, Skipped;
        public double Seconds;

        public bool HasWork
        {
            get
            {
                if (Kind == RuleKind.Info) return false;
                if (Kind == RuleKind.ComponentCleanup) return true;
                return Size > 0;
            }
        }

        // Rules whose files can be reviewed one by one
        public bool IsFileList { get { return Kind == RuleKind.Files || Kind == RuleKind.ScanBackups; } }

        public long SelSize;
        public int SelCount;
        public int DisplayCount = -1;

        public void Recalc()
        {
            long s = 0;
            int c = 0;
            List<FileHit> hits = Hits;
            foreach (FileHit h in hits)
                if (h.Selected) { s += h.Size; c++; }
            SelSize = s;
            SelCount = c;
        }

        // Something will actually be done if this rule is cleaned
        public bool HasSelection { get { if (DisplayCount >= 0) return Size > 0; return IsFileList ? SelCount > 0 : HasWork; } }

        public long SelectedBytes
        {
            get
            {
                if (IsFileList) return SelSize;
                return Kind == RuleKind.ComponentCleanup ? 0 : Size;
            }
        }
    }

    static class CleanupRules
    {
        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        static readonly string ProgData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string SysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        // All real user profile folders (C:\Users\name)
        static List<string> Profiles()
        {
            var list = new List<string>();
            try
            {
                string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                foreach (DirectoryInfo d in new DirectoryInfo(users).GetDirectories())
                {
                    try
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (d.Name.Equals("Public", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(d.FullName);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        static List<string> InProfiles(params string[] relative)
        {
            var list = new List<string>();
            foreach (string p in Profiles())
                foreach (string r in relative) list.Add(Path.Combine(p, r));
            return list;
        }

        static List<string> SubDirs(string path)
        {
            var list = new List<string>();
            try { foreach (string d in Directory.GetDirectories(path)) list.Add(d); } catch { }
            return list;
        }

        static List<string> BrowserCaches()
        {
            var list = new List<string>();
            foreach (string p in Profiles())
            {
                string[] chromium = {
                    Path.Combine(p, @"AppData\Local\Google\Chrome\User Data"),
                    Path.Combine(p, @"AppData\Local\Microsoft\Edge\User Data")
                };
                foreach (string b in chromium)
                    foreach (string prof in SubDirs(b))
                        foreach (string c in new[] { "Cache", "Code Cache", "GPUCache" })
                            list.Add(Path.Combine(prof, c));
                foreach (string ff in SubDirs(Path.Combine(p, @"AppData\Local\Mozilla\Firefox\Profiles")))
                    list.Add(Path.Combine(ff, "cache2"));
                list.Add(Path.Combine(p, @"AppData\Local\Microsoft\Windows\INetCache"));
            }
            return list;
        }

        static List<string> RecycleBins()
        {
            var list = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                    list.AddRange(SubDirs(Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin")));
                }
                catch { }
            }
            return list;
        }

        static HashSet<string> Names(params string[] n)
        {
            return new HashSet<string>(n, StringComparer.OrdinalIgnoreCase);
        }

        public static List<CleanupRule> Build()
        {
            var r = new List<CleanupRule>();

            // ---------------- Safe ----------------
            r.Add(new CleanupRule
            {
                Title = "Temporary files of all users",
                Description = "Leftover files that programs create while running. Only files older than 2 days are removed.",
                Level = Safety.Safe, MinAgeDays = 2, RemoveEmptyFolders = true,
                Targets = () =>
                {
                    List<string> t = InProfiles(@"AppData\Local\Temp");
                    t.Add(Path.Combine(Win, @"ServiceProfiles\LocalService\AppData\Local\Temp"));
                    t.Add(Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Temp"));
                    t.Add(Path.Combine(Win, @"System32\config\systemprofile\AppData\Local\Temp"));
                    return t;
                },
                OpenPath = Path.GetTempPath()
            });
            r.Add(new CleanupRule
            {
                Title = "Windows temporary files",
                Description = "Temporary files in the Windows Temp folder older than 2 days.",
                Level = Safety.Safe, MinAgeDays = 2, RemoveEmptyFolders = true,
                Targets = () => new List<string> { Path.Combine(Win, "Temp") },
                OpenPath = Path.Combine(Win, "Temp")
            });
            r.Add(new CleanupRule
            {
                Title = "Windows Update downloads",
                Description = "Update files Windows already downloaded. Windows downloads them again if it still needs them.",
                Level = Safety.Safe, MinAgeDays = 1, RemoveEmptyFolders = true,
                Targets = () => new List<string> { Path.Combine(Win, @"SoftwareDistribution\Download") },
                OpenPath = Path.Combine(Win, @"SoftwareDistribution\Download")
            });
            r.Add(new CleanupRule
            {
                Title = "Delivery Optimization cache",
                Description = "Copies of updates Windows keeps to share with other computers. Not needed on VMs.",
                Level = Safety.Safe, RemoveEmptyFolders = true,
                Targets = () => new List<string> {
                    Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
                    Path.Combine(Win, @"SoftwareDistribution\DeliveryOptimization") },
                OpenPath = Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")
            });
            r.Add(new CleanupRule
            {
                Title = "Error reports",
                Description = "Reports Windows collected about program crashes. Useful only if you are sending them to Microsoft.",
                Level = Safety.Safe, RemoveEmptyFolders = true,
                Targets = () =>
                {
                    var t = new List<string> {
                        Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"),
                        Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue"),
                        Path.Combine(ProgData, @"Microsoft\Windows\WER\Temp") };
                    t.AddRange(InProfiles(@"AppData\Local\Microsoft\Windows\WER"));
                    return t;
                },
                OpenPath = Path.Combine(ProgData, @"Microsoft\Windows\WER")
            });
            r.Add(new CleanupRule
            {
                Title = "Browser caches of all users",
                Description = "Saved web page data from Chrome, Edge, Firefox and Internet Explorer. Browsers rebuild it as needed.",
                Level = Safety.Safe, RemoveEmptyFolders = true,
                Targets = BrowserCaches
            });
            r.Add(new CleanupRule
            {
                Title = "Recycle Bin of all users",
                Description = "Files people already deleted on every drive. They can't be restored after this.",
                Level = Safety.Safe, RemoveEmptyFolders = true, Exclude = Names("desktop.ini"),
                Targets = RecycleBins
            });

            // ---------------- Check first ----------------
            r.Add(new CleanupRule
            {
                Title = "Crash dump files",
                Description = "Memory dumps saved when Windows or a program crashed. Keep them if someone is investigating a crash.",
                Level = Safety.Review, Pattern = "*.dmp",
                Targets = () =>
                {
                    var t = new List<string> { Path.Combine(Win, "MEMORY.DMP"), Path.Combine(Win, "Minidump"), Path.Combine(Win, "LiveKernelReports") };
                    t.AddRange(InProfiles(@"AppData\Local\CrashDumps"));
                    return t;
                },
                OpenPath = Path.Combine(Win, "Minidump")
            });
            r.Add(new CleanupRule
            {
                Title = "Web server (IIS) logs older than 30 days",
                Description = "Old website access logs. Check that your company doesn't need to keep them for audits.",
                Level = Safety.Review, Pattern = "*.log", MinAgeDays = 30,
                Targets = () => new List<string> { Path.Combine(SysDrive, @"inetpub\logs\LogFiles") },
                OpenPath = Path.Combine(SysDrive, @"inetpub\logs\LogFiles")
            });
            r.Add(new CleanupRule
            {
                Title = "Downloads older than 90 days",
                Description = "Files in users' Downloads folders that nobody has touched for 3 months. Often old installers.",
                Level = Safety.Review, MinAgeDays = 90,
                Targets = () => InProfiles("Downloads")
            });
            r.Add(new CleanupRule
            {
                Title = "Backup files (.bak) older than 30 days",
                Description = "Old database or program backups over 10 MB, found by Scan drives. Run a scan first to find them.",
                Level = Safety.Review, Kind = RuleKind.ScanBackups, MinAgeDays = 30
            });
            r.Add(new CleanupRule
            {
                Title = "Hibernation file",
                Description = "A file as large as the VM's memory, used only for hibernating. Cleaning turns hibernation off.",
                Level = Safety.Review, Kind = RuleKind.Hibernation
            });
            r.Add(new CleanupRule
            {
                Title = "Windows component cleanup (WinSxS)",
                Description = "Asks Windows to remove old versions of updated system files. Supported by Microsoft, but takes 10 to 30 minutes.",
                Level = Safety.Review, Kind = RuleKind.ComponentCleanup
            });

            // ---------------- Leave alone ----------------
            r.Add(new CleanupRule
            {
                Title = "Windows Installer folder",
                Description = "C:\\Windows\\Installer looks like junk but is needed to patch and uninstall programs. Never delete it by hand.",
                Level = Safety.LeaveAlone, Kind = RuleKind.Info
            });
            r.Add(new CleanupRule
            {
                Title = "WinSxS folder",
                Description = "Windows system components. Deleting files here breaks Windows. Use the component cleanup item instead.",
                Level = Safety.LeaveAlone, Kind = RuleKind.Info
            });
            r.Add(new CleanupRule
            {
                Title = "Page file (pagefile.sys)",
                Description = "Windows' virtual memory. To make it smaller, change the virtual memory setting in System Properties.",
                Level = Safety.LeaveAlone, Kind = RuleKind.Info
            });

            int colorIdx = 0;
            foreach (CleanupRule rule in r)
            {
                rule.Color = rule.Kind == RuleKind.Info ? Theme.Gray : Theme.Pick(colorIdx++);
                rule.Checked = rule.Level == Safety.Safe;
            }
            return r;
        }
    }

    class Advisor
    {
        public volatile bool Cancel;
        public volatile string Status = "";

        public bool UseIndex = true;
        Dictionary<string, MftIndex> indexes = new Dictionary<string, MftIndex>(StringComparer.OrdinalIgnoreCase);
        public int IndexedDrives;

        // Reads each drive's index once per check. Returns null if that isn't possible.
        MftIndex GetIndex(string path)
        {
            if (!UseIndex) return null;
            string drive;
            try { drive = Path.GetPathRoot(path); } catch { return null; }
            if (string.IsNullOrEmpty(drive) || drive.StartsWith(@"\\")) return null;
            MftIndex idx;
            if (indexes.TryGetValue(drive, out idx)) return idx;
            idx = null;
            try
            {
                if (string.Equals(new DriveInfo(drive).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    idx = new MftReader(() => Cancel, t => Status = "Reading " + t + "...").ReadIndex(drive);
                    IndexedDrives++;
                }
            }
            catch { idx = null; }
            indexes[drive] = idx;
            return idx;
        }

        static Func<string, bool> PatternMatcher(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*") return n => true;
            if (pattern.StartsWith("*.") && pattern.IndexOfAny(new[] { '*', '?' }, 2) < 0)
            {
                string ext = pattern.Substring(1);
                return n => n.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
            }
            var rx = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
            return n => rx.IsMatch(n);
        }

        public void Analyze(List<CleanupRule> rules, List<FileEntry> scanFiles)
        {
            try { AnalyzeAll(rules, scanFiles); }
            finally
            {
                indexes = new Dictionary<string, MftIndex>(StringComparer.OrdinalIgnoreCase);   // free the memory
                GC.Collect();
            }
        }

        void AnalyzeAll(List<CleanupRule> rules, List<FileEntry> scanFiles)
        {
            foreach (CleanupRule r in rules) { r.Analyzed = false; }
            foreach (CleanupRule r in rules)
            {
                if (Cancel) break;
                Status = "Checking: " + r.Title + "...";
                var unticked = new HashSet<string>(r.Hits.Where(h => !h.Selected).Select(h => h.Path),
                    StringComparer.OrdinalIgnoreCase);
                var hits = new List<FileHit>();
                long size = 0;
                try { AnalyzeRule(r, scanFiles, hits, ref size); } catch { }
                foreach (FileHit h in hits)
                    if (unticked.Contains(h.Path)) h.Selected = false;
                r.Hits = hits;
                r.Size = size;
                r.Recalc();
                r.Analyzed = true;
            }
            Status = "";
        }

        void AnalyzeRule(CleanupRule r, List<FileEntry> scanFiles, List<FileHit> hits, ref long size)
        {
            DateTime cutoff = DateTime.Now.AddDays(-r.MinAgeDays);
            switch (r.Kind)
            {
                case RuleKind.Files:
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Func<string, bool> nameOk = PatternMatcher(r.Pattern);
                    foreach (string t in r.Targets())
                    {
                        if (!seen.Add(t)) continue;

                        // Fast way: look the folder up in the disk index
                        MftIndex idx = GetIndex(t);
                        int rec = idx != null ? idx.Find(t) : -1;
                        if (rec >= 0)
                        {
                            long found = 0;
                            idx.ForEachFile(rec, t, () => Cancel, (full, name, len, mod, created) =>
                            {
                                if (!nameOk(name)) return;
                                if (r.Exclude != null && r.Exclude.Contains(name)) return;
                                if (r.MinAgeDays > 0 && !(mod < cutoff && created < cutoff)) return;
                                hits.Add(new FileHit { Path = full, Size = len, Modified = mod });
                                found += len;
                            });
                            size += found;
                            continue;
                        }

                        // Normal way (not NTFS, or the folder is reached through a shortcut/junction)
                        if (File.Exists(t)) AddFile(r, new FileInfo(t), cutoff, hits, ref size);
                        else if (Directory.Exists(t)) Collect(new DirectoryInfo(t), r, cutoff, hits, ref size);
                    }
                    break;

                case RuleKind.ScanBackups:
                    if (scanFiles == null) break;
                    foreach (FileEntry f in scanFiles)
                    {
                        if (f.Ext == ".bak" && f.Modified != DateTime.MinValue && f.Modified < cutoff && File.Exists(f.FullPath))
                        {
                            hits.Add(new FileHit { Path = f.FullPath, Size = f.Size, Modified = f.Modified });
                            size += f.Size;
                        }
                    }
                    break;

                case RuleKind.Hibernation:
                    FileInfo[] hib = new DirectoryInfo(CleanupRules.SysDrive).GetFiles("hiberfil.sys");
                    if (hib.Length > 0)
                    {
                        hits.Add(new FileHit { Path = hib[0].FullName, Size = hib[0].Length, Modified = hib[0].LastWriteTime });
                        size += hib[0].Length;
                    }
                    break;
            }
        }

        static bool IsOld(FileInfo f, DateTime cutoff, int minAge)
        {
            if (minAge <= 0) return true;
            return f.LastWriteTime < cutoff && f.CreationTime < cutoff;
        }

        void AddFile(CleanupRule r, FileInfo f, DateTime cutoff, List<FileHit> hits, ref long size)
        {
            try
            {
                if (r.Exclude != null && r.Exclude.Contains(f.Name)) return;
                if (!IsOld(f, cutoff, r.MinAgeDays)) return;
                long len = f.Length;
                hits.Add(new FileHit { Path = f.FullName, Size = len, Modified = f.LastWriteTime });
                size += len;
            }
            catch { }
        }

        void Collect(DirectoryInfo dir, CleanupRule r, DateTime cutoff, List<FileHit> hits, ref long size)
        {
            if (Cancel) return;
            try
            {
                foreach (FileInfo f in dir.EnumerateFiles(r.Pattern)) AddFile(r, f, cutoff, hits, ref size);
            }
            catch { }
            try
            {
                foreach (DirectoryInfo d in dir.EnumerateDirectories())
                {
                    try { if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; }
                    catch { continue; }
                    Collect(d, r, cutoff, hits, ref size);
                }
            }
            catch { }
        }

        public void Clean(List<CleanupRule> todo)
        {
            foreach (CleanupRule r in todo)
            {
                r.Freed = 0; r.Deleted = 0; r.Skipped = 0; r.Seconds = 0;
                if (Cancel) break;
                switch (r.Kind)
                {
                    case RuleKind.Files:
                    case RuleKind.ScanBackups:
                        var watch = Stopwatch.StartNew();
                        DeleteInParallel(r);
                        if (r.RemoveEmptyFolders && r.Targets != null && !Cancel)
                        {
                            Status = "Cleaning: " + r.Title + " (removing empty folders)...";
                            foreach (string t in r.Targets().Distinct(StringComparer.OrdinalIgnoreCase))
                                if (Directory.Exists(t)) RemoveEmptyDirs(new DirectoryInfo(t), true);
                        }
                        r.Seconds = watch.Elapsed.TotalSeconds;
                        break;

                    case RuleKind.Hibernation:
                        Status = "Turning off hibernation...";
                        RunHidden("powercfg.exe", "/hibernate off", 5);
                        if (r.Hits.Count > 0 && !File.Exists(r.Hits[0].Path)) { r.Freed = r.Size; r.Deleted = 1; }
                        break;

                    case RuleKind.ComponentCleanup:
                        Status = "Running Windows component cleanup. This can take 10 to 30 minutes, please keep the app open...";
                        long before = FreeSpace();
                        RunHidden(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
                            "/Online /Cleanup-Image /StartComponentCleanup", 120);
                        r.Freed = Math.Max(0, FreeSpace() - before);
                        break;
                }
            }
            Status = "";
        }

        static long FreeSpace()
        {
            try { return new DriveInfo(CleanupRules.SysDrive).TotalFreeSpace; } catch { return 0; }
        }

        // ---- fast delete ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFileW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetFileAttributesW(string path, uint attributes);

        const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5;

        // One direct call to Windows per file. Only if that fails because the file
        // is read-only do we clear the attribute and try once more.
        static bool FastDelete(string path, out bool alreadyGone)
        {
            alreadyGone = false;
            // \\?\ lets Windows handle paths longer than 260 characters
            string p = path.Length >= 240 && !path.StartsWith(@"\\") ? @"\\?\" + path : path;
            if (DeleteFileW(p)) return true;
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND) { alreadyGone = true; return true; }
            if (err == ERROR_ACCESS_DENIED && SetFileAttributesW(p, 0x80) && DeleteFileW(p)) return true;
            return false;
        }

        // Deletes the ticked files of one rule using several threads at once.
        // Files are grouped by folder so the threads don't wait on each other.
        void DeleteInParallel(CleanupRule r)
        {
            List<FileHit> todo = r.Hits.Where(h => h.Selected).ToList();
            int total = todo.Count;
            if (total == 0) return;

            var work = new List<List<FileHit>>();
            foreach (IGrouping<string, FileHit> g in todo.GroupBy(h => h.Folder, StringComparer.OrdinalIgnoreCase))
            {
                List<FileHit> batch = null;
                foreach (FileHit h in g)
                {
                    if (batch == null || batch.Count >= 256) { batch = new List<FileHit>(); work.Add(batch); }
                    batch.Add(h);
                }
            }

            long freed = 0;
            int deleted = 0, skipped = 0, done = 0;
            var options = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount * 2))
            };
            System.Threading.Tasks.Parallel.ForEach(work, options, (batch, state) =>
            {
                foreach (FileHit h in batch)
                {
                    if (Cancel) { state.Stop(); return; }
                    bool gone;
                    if (FastDelete(h.Path, out gone))
                    {
                        Interlocked.Increment(ref deleted);
                        if (!gone) Interlocked.Add(ref freed, h.Size);
                    }
                    else Interlocked.Increment(ref skipped);
                    int d = Interlocked.Increment(ref done);
                    if ((d & 255) == 1)
                        Status = string.Format("Cleaning: {0} ({1:N0} of {2:N0} files)...", r.Title, d, total);
                }
            });
            r.Freed = freed;
            r.Deleted = deleted;
            r.Skipped = skipped;
        }

        static void RemoveEmptyDirs(DirectoryInfo dir, bool isRoot)
        {
            try
            {
                foreach (DirectoryInfo d in dir.GetDirectories())
                {
                    try { if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; }
                    catch { continue; }
                    RemoveEmptyDirs(d, false);
                }
                if (!isRoot && dir.GetFileSystemInfos().Length == 0) dir.Delete();
            }
            catch { }
        }

        static void RunHidden(string exe, string args, int timeoutMinutes)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(timeoutMinutes * 60 * 1000);
                }
            }
            catch { }
        }
    }

    // One row in the Cleanup Advisor list
    class AdvisorRow : Canvas
    {
        public CleanupRule Rule;
        public event Action Toggled;
        public event Action ViewRequested;
        bool hover;
        static readonly Font LinkFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Underline);

        public AdvisorRow(CleanupRule rule)
        {
            Rule = rule;
            Height = 78;
            Margin = new Padding(0, 0, 0, 8);
            BackColor = Theme.Bg;
        }

        bool Checkable { get { return Enabled && Rule.Analyzed && Rule.HasWork; } }
        bool Viewable { get { return Enabled && Rule.Analyzed && Rule.IsFileList && Rule.Hits.Count > 0; } }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            Tip.SetToolTip(this, Viewable
                ? "Click to see these files and choose which ones to delete.\nClick the box on the left to include or skip the whole item."
                : "");
            base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = (Checkable || Viewable) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            bool onCheckbox = e.X < 48;
            if (!onCheckbox && Viewable)
            {
                if (ViewRequested != null) ViewRequested();
                return;
            }
            if (!Checkable) return;
            Rule.Checked = !Rule.Checked;
            if (Rule.Checked && Rule.IsFileList && Rule.SelCount == 0)
            {
                foreach (FileHit h in Rule.Hits) h.Selected = true;
                Rule.Recalc();
            }
            Invalidate();
            if (Toggled != null) Toggled();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Hq(g);
            g.Clear(Theme.Bg);
            bool info = Rule.Kind == RuleKind.Info;
            bool on = Rule.Checked && Rule.Analyzed && Rule.HasSelection;

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color border = on ? Theme.Lighten(Theme.Accent, 0.35f) : (hover && Checkable ? Theme.Faint : Theme.Border);
            using (GraphicsPath path = Util.RoundRect(r, 10))
            using (var b = new SolidBrush(info ? Theme.SurfaceAlt : Theme.Surface))
            using (var p = new Pen(border, on ? 1.5f : 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            if (on)
            {
                using (GraphicsPath strip = Util.RoundRect(new RectangleF(5, 14, 4, Height - 28), 2))
                using (var b = new SolidBrush(Rule.Color))
                    g.FillPath(b, strip);
            }

            // checkbox or info mark
            var cb = new RectangleF(20, Height / 2f - 9, 18, 18);
            if (info)
            {
                using (var p = new Pen(Theme.Faint, 1.5f)) g.DrawEllipse(p, cb);
                TextRenderer.DrawText(g, "i", Theme.SmallBold, Rectangle.Round(cb), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                using (GraphicsPath path = Util.RoundRect(cb, 5))
                {
                    if (on)
                    {
                        using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                        using (var p = new Pen(Color.White, 2f))
                            g.DrawLines(p, new[] {
                                new PointF(cb.X + 4, cb.Y + 9.5f), new PointF(cb.X + 7.5f, cb.Y + 13), new PointF(cb.X + 14, cb.Y + 5.5f) });
                    }
                    else
                    {
                        if (!Checkable) using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, path);
                        using (var p = new Pen(Theme.Faint, 1.5f)) g.DrawPath(p, path);
                    }
                }
            }

            // title + badge
            int x = 54;
            TextRenderer.DrawText(g, Rule.Title, Theme.BodyBold, new Point(x, 13), Theme.Ink, TextFormatFlags.NoPrefix);
            int tw = TextRenderer.MeasureText(Rule.Title, Theme.BodyBold).Width;
            string badge;
            Color badgeBg, badgeFg;
            if (Rule.Level == Safety.Safe) { badge = "Safe"; badgeBg = Color.FromArgb(227, 244, 236); badgeFg = Color.FromArgb(30, 122, 80); }
            else if (Rule.Level == Safety.Review) { badge = "Check first"; badgeBg = Color.FromArgb(252, 241, 221); badgeFg = Color.FromArgb(154, 91, 11); }
            else { badge = "Leave alone"; badgeBg = Color.FromArgb(232, 235, 240); badgeFg = Theme.Muted; }
            int bw = TextRenderer.MeasureText(badge, Theme.SmallBold).Width + 14;
            var br = new RectangleF(x + tw + 8, 13, bw, 20);
            using (GraphicsPath path = Util.RoundRect(br, 10))
            using (var b = new SolidBrush(badgeBg))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, badge, Theme.SmallBold, Rectangle.Round(br), badgeFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int rightW = info ? 0 : 170;
            TextRenderer.DrawText(g, Rule.Description, Theme.Small, new Rectangle(x, 37, Width - x - rightW - 16, 34), Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (info) return;

            // size on the right
            var sizeRect = new Rectangle(Width - rightW - 16, 12, rightW, 28);
            var subRect = new Rectangle(Width - rightW - 16, 40, rightW, 20);
            TextFormatFlags rf = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (!Rule.Analyzed)
                TextRenderer.DrawText(g, "Checking...", Theme.Body, sizeRect, Theme.Faint, rf);
            else if (Rule.Kind == RuleKind.ComponentCleanup)
            {
                TextRenderer.DrawText(g, "Size varies", Theme.BodyBold, sizeRect, Theme.Ink, rf);
                TextRenderer.DrawText(g, "measured by Windows", Theme.Small, subRect, Theme.Muted, rf);
            }
            else if (Rule.Size <= 0)
            {
                string none = Rule.Kind == RuleKind.ScanBackups && Rule.Hits.Count == 0 ? "None found" : "Nothing to clean";
                if (Rule.Kind == RuleKind.Hibernation) none = "Already off";
                TextRenderer.DrawText(g, none, Theme.Body, sizeRect, Theme.Faint, rf);
            }
            else if (Rule.IsFileList)
            {
                bool partial = Rule.DisplayCount < 0 && Rule.SelCount < Rule.Hits.Count;
                TextRenderer.DrawText(g, Util.Size(partial ? Rule.SelSize : Rule.Size), Theme.Big, sizeRect, Theme.Ink, rf);
                int shownCount = Rule.DisplayCount >= 0 ? Rule.DisplayCount : Rule.Hits.Count;
                string link = Rule.DisplayCount >= 0
                    ? string.Format("{0:N0} file{1}", shownCount, shownCount == 1 ? "" : "s")
                    : partial
                    ? string.Format("{0:N0} of {1:N0} files ticked, review", Rule.SelCount, Rule.Hits.Count)
                    : string.Format("Review {0:N0} file{1}", Rule.Hits.Count, Rule.Hits.Count == 1 ? "" : "s");
                TextRenderer.DrawText(g, link, hover && Enabled ? LinkFont : Theme.SmallBold, subRect, Theme.Accent, rf);
            }
            else
            {
                TextRenderer.DrawText(g, Util.Size(Rule.Size), Theme.Big, sizeRect, Theme.Ink, rf);
                TextRenderer.DrawText(g, Rule.Hits.Count.ToString("N0") + (Rule.Hits.Count == 1 ? " file" : " files"),
                    Theme.Small, subRect, Theme.Muted, rf);
            }
        }
    }

    // ------------------------------------------------------------------
    //  Review window: see every file in a cleanup item and tick what to delete
    // ------------------------------------------------------------------
    class RuleFilesForm : Form
    {
        readonly CleanupRule rule;
        List<FileHit> view = new List<FileHit>();
        DataGridView grid;
        SearchBox search;
        Label lblSel;
        FlatBtn btnDelete;
        System.Windows.Forms.Timer filterTimer;
        FilterSpec filter = new FilterSpec();
        int sortCol = 2;
        bool sortDesc = true;
        public bool Changed;

        public RuleFilesForm(CleanupRule r)
        {
            rule = r;
            Text = "Review files - " + r.Title;
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            Font = Theme.Body;
            Size = new Size(1100, 700);
            MinimumSize = new Size(820, 500);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- list ----
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0), BackColor = Theme.Bg };
            var card = new Card
            {
                Title = "Files",
                Subtitle = "Tick the files to delete. Click a column title to sort. Double-click a file to see it in Explorer."
            };
            grid = GridStyle.Make();
            grid.VirtualMode = true;
            var chk = new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 44, SortMode = DataGridViewColumnSortMode.NotSortable };
            chk.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(chk);
            grid.Columns.Add(GridStyle.Col("name", "Name", 0, 34, false));
            grid.Columns.Add(GridStyle.Col("size", "Size", 96, 0, true));
            grid.Columns.Add(GridStyle.Col("modified", "Last changed", 130, 0, false));
            grid.Columns.Add(GridStyle.Col("folder", "Folder", 0, 66, false));
            foreach (DataGridViewColumn c in grid.Columns)
                if (c.Index > 0) c.SortMode = DataGridViewColumnSortMode.Programmatic;
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.CellClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0) Toggle(new[] { e.RowIndex });
            };
            grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex > 0 && e.RowIndex < view.Count)
                    Util.OpenInExplorer(view[e.RowIndex].Path, true);
            };
            grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space)
                {
                    Toggle(grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Index).OrderBy(x => x).ToArray());
                    e.Handled = true;
                }
            };
            grid.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.ColumnIndex == 0) return;
                if (sortCol == e.ColumnIndex) sortDesc = !sortDesc;
                else { sortCol = e.ColumnIndex; sortDesc = e.ColumnIndex == 2 || e.ColumnIndex == 3; }
                RebuildView();
            };
            card.Controls.Add(grid);
            body.Controls.Add(card);

            // ---- header ----
            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Bg };
            var lblTitle = new Label
            {
                Text = r.Title, Font = Theme.Big, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(18, 16)
            };
            var lblDesc = new Label
            {
                Text = r.Description + "  Deleted files do not go to the Recycle Bin.",
                ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true, Location = new Point(20, 48), Size = new Size(600, 36)
            };
            search = new SearchBox("Filter: name, .log, older than 1 year, >100mb");
            search.Size = new Size(340, 34);
            search.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            filterTimer = new System.Windows.Forms.Timer { Interval = 300 };
            filterTimer.Tick += (s, e) => { filterTimer.Stop(); filter = FilterSpec.Parse(search.Box.Text); RebuildView(); };
            search.Box.TextChanged += (s, e) => { filterTimer.Stop(); filterTimer.Start(); };
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblDesc);
            header.Controls.Add(search);
            header.Resize += (s, e) =>
            {
                search.Location = new Point(header.Width - search.Width - 20, 20);
                lblDesc.Width = Math.Max(200, header.Width - search.Width - 60);
            };

            // ---- footer ----
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, BackColor = Theme.Bg };
            lblSel = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 0, 0, 0),
                ForeColor = Theme.Ink, Font = Theme.BodyBold, AutoEllipsis = true
            };
            var btnDone = new FlatBtn("Done", BtnKind.Primary);
            btnDelete = new FlatBtn("Delete ticked files now", BtnKind.Danger);
            var btnShow = new FlatBtn("Show in Explorer", BtnKind.Secondary);
            var btnNone = new FlatBtn("Untick all shown", BtnKind.Secondary);
            var btnAll = new FlatBtn("Tick all shown", BtnKind.Secondary);
            btnDone.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            btnDelete.Click += (s, e) =>
            {
                rule.Recalc();
                if (rule.SelCount == 0)
                {
                    MessageBox.Show(this, "Tick at least one file first.", "Review files", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.Yes;
                Close();
            };
            btnShow.Click += (s, e) =>
            {
                if (grid.CurrentRow != null && grid.CurrentRow.Index < view.Count)
                    Util.OpenInExplorer(view[grid.CurrentRow.Index].Path, true);
            };
            btnNone.Click += (s, e) => SetAll(false);
            btnAll.Click += (s, e) => SetAll(true);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
                BackColor = Theme.Bg, Padding = new Padding(0, 16, 20, 0)
            };
            int w = 24;
            foreach (Control c in new Control[] { btnDone, btnDelete, btnShow, btnNone, btnAll })
            {
                flow.Controls.Add(c);
                w += c.Width + c.Margin.Horizontal;
            }
            flow.Width = w;
            footer.Controls.Add(lblSel);
            footer.Controls.Add(flow);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.OK; Close(); } };
            Shown += (s, e) => { RebuildView(); grid.Focus(); };
        }

        void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= view.Count) return;
            FileHit h = view[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = h.Selected; break;
                case 1: e.Value = h.Name; break;
                case 2: e.Value = Util.Size(h.Size); break;
                case 3: e.Value = h.Modified == DateTime.MinValue ? "" : h.Modified.ToString("yyyy-MM-dd HH:mm"); break;
                case 4: e.Value = h.Folder; break;
            }
        }

        void RebuildView()
        {
            IEnumerable<FileHit> q = rule.Hits;
            if (!filter.IsEmpty) q = q.Where(filter.MatchHit);
            switch (sortCol)
            {
                case 1: q = sortDesc ? q.OrderByDescending(h => h.Name, StringComparer.OrdinalIgnoreCase) : q.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase); break;
                case 3: q = sortDesc ? q.OrderByDescending(h => h.Modified) : q.OrderBy(h => h.Modified); break;
                case 4: q = sortDesc ? q.OrderByDescending(h => h.Folder, StringComparer.OrdinalIgnoreCase) : q.OrderBy(h => h.Folder, StringComparer.OrdinalIgnoreCase); break;
                default: q = sortDesc ? q.OrderByDescending(h => h.Size) : q.OrderBy(h => h.Size); break;
            }
            view = q.ToList();

            foreach (DataGridViewColumn c in grid.Columns)
                if (c.Index > 0) c.HeaderCell.SortGlyphDirection = SortOrder.None;
            if (grid.Columns[sortCol].SortMode != DataGridViewColumnSortMode.NotSortable)
                grid.Columns[sortCol].HeaderCell.SortGlyphDirection = sortDesc ? SortOrder.Descending : SortOrder.Ascending;

            grid.RowCount = 0;
            grid.RowCount = view.Count;
            grid.ClearSelection();
            grid.Invalidate();
            UpdateSel();
        }

        void Toggle(int[] rows)
        {
            if (rows.Length == 0 || rows[0] >= view.Count) return;
            bool val = !view[rows[0]].Selected;
            foreach (int i in rows)
                if (i >= 0 && i < view.Count) view[i].Selected = val;
            Changed = true;
            grid.Invalidate();
            UpdateSel();
        }

        void SetAll(bool val)
        {
            foreach (FileHit h in view) h.Selected = val;
            Changed = true;
            grid.Invalidate();
            UpdateSel();
        }

        void UpdateSel()
        {
            rule.Recalc();
            string shown = filter.IsEmpty ? ""
                : string.Format("   Showing {0:N0} files that match your filter.", view.Count);
            lblSel.Text = string.Format("Ticked {0:N0} of {1:N0} files: {2} of {3}.{4}",
                rule.SelCount, rule.Hits.Count, Util.Size(rule.SelSize), Util.Size(rule.Size), shown);
            btnDelete.Enabled = rule.SelCount > 0;
        }
    }

    // ==================================================================
    //  OLD-FILE OWNER REPORTS
    //  Finds files older than N months in folders you list, and emails
    //  each folder's owner a report. It never deletes anything.
    // ==================================================================
    class OwnerEntry
    {
        public string Folder, Email, Name;
        public List<FileEntry> Files = new List<FileEntry>();
        public long Size;
        public string Status = "";
        public bool Found;      // folder exists
    }

    static class OwnerConfig
    {
        public static string MapPath { get { return Path.Combine(Application.StartupPath, "OwnerMap.csv"); } }
        public static string EmailPath { get { return Path.Combine(Application.StartupPath, "EmailSettings.ini"); } }
        public static string DraftsDir { get { return Path.Combine(Application.StartupPath, "OldFileReports"); } }

        public static void EnsureTemplates()
        {
            if (!File.Exists(MapPath))
            {
                File.WriteAllText(MapPath,
                    "# Old-file owner map for Space Analyzer\r\n" +
                    "# One line per folder. Columns:  Folder , Email , Name (name is optional)\r\n" +
                    "# Lines starting with # are ignored. Use the real folder path, local or network.\r\n" +
                    "#\r\n" +
                    "# Examples (delete these and add your own):\r\n" +
                    "D:\\Projects\\Alpha , alice@example.com , Alice\r\n" +
                    "D:\\Projects\\Bravo , bob@example.com , Bob\r\n" +
                    "\\\\fileserver\\Shares\\Finance , finance-team@example.com , Finance Team\r\n",
                    Encoding.UTF8);
            }
            if (!File.Exists(EmailPath))
            {
                File.WriteAllText(EmailPath,
                    "; Email settings for Space Analyzer old-file reports.\r\n" +
                    "; Fill these in to send email. You can skip this and use \"Save drafts\" instead,\r\n" +
                    "; which writes .eml files you can open and send from Outlook.\r\n" +
                    ";\r\n" +
                    "SmtpServer = \r\n" +
                    "Port       = 25\r\n" +
                    "UseSsl     = false\r\n" +
                    "From       = spacereports@example.com\r\n" +
                    "; Username and Password are only needed if your mail server requires sign-in.\r\n" +
                    "Username   = \r\n" +
                    "Password   = \r\n" +
                    "; CcToSelf sends a copy of every report to the From address.\r\n" +
                    "CcToSelf   = false\r\n",
                    Encoding.UTF8);
            }
        }

        public static List<OwnerEntry> LoadMap(out string error)
        {
            error = null;
            var list = new List<OwnerEntry>();
            try
            {
                foreach (string raw in File.ReadAllLines(MapPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split(',');
                    if (p.Length < 2) continue;
                    string folder = p[0].Trim();
                    string email = p[1].Trim();
                    if (folder.Length == 0 || email.Length == 0) continue;
                    // skip a header row like "Folder,Email,Name"
                    if (email.IndexOf('@') < 0 && folder.Equals("folder", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new OwnerEntry
                    {
                        Folder = folder.TrimEnd('\\'),
                        Email = email,
                        Name = p.Length >= 3 ? p[2].Trim() : ""
                    });
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return list;
        }

        public static Dictionary<string, string> LoadEmail()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string raw in File.ReadAllLines(EmailPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return d;
        }
    }

    class OwnerScanner
    {
        public volatile bool Cancel;
        public volatile string Status = "";
        readonly int ageDays;
        public OwnerScanner(int days) { ageDays = days; }

        public void Scan(List<OwnerEntry> owners)
        {
            DateTime cutoff = DateTime.Now.AddDays(-ageDays);
            var indexes = new Dictionary<string, MftIndex>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (OwnerEntry o in owners)
                {
                    if (Cancel) break;
                    o.Files.Clear();
                    o.Size = 0;
                    Status = "Checking " + o.Folder;
                    if (!Directory.Exists(o.Folder))
                    {
                        o.Found = false;
                        o.Status = "Folder not found";
                        continue;
                    }
                    o.Found = true;
                    try { ScanOne(o, cutoff, indexes); }
                    catch (Exception ex) { o.Status = "Could not read: " + ex.Message; }
                    o.Status = o.Files.Count == 0
                        ? "No files older than " + ageDays + " days"
                        : string.Format("{0:N0} old files, {1}", o.Files.Count, Util.Size(o.Size));
                }
            }
            finally { Status = ""; }
        }

        void ScanOne(OwnerEntry o, DateTime cutoff, Dictionary<string, MftIndex> indexes)
        {
            // Fast path: local NTFS drive -> read the disk index once per drive
            MftIndex idx = GetIndex(o.Folder, indexes);
            int rec = idx != null ? idx.Find(o.Folder) : -1;
            if (rec >= 0 && idx.IsDir(rec))
            {
                long size = 0;
                idx.ForEachFile(rec, o.Folder, () => Cancel, (full, name, len, mod, created) =>
                {
                    if (mod != DateTime.MinValue && mod < cutoff)
                    {
                        o.Files.Add(new FileEntry { Name = name, FullPath = full, Ext = Scanner.ExtOf(name).ToLowerInvariant(), Size = len, Modified = mod });
                        size += len;
                    }
                });
                o.Size = size;
                return;
            }

            // Normal path: network share, non-NTFS, or junction target
            var stack = new Stack<string>();
            stack.Push(o.Folder);
            while (stack.Count > 0)
            {
                if (Cancel) return;
                string dir = stack.Pop();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (fi.LastWriteTime < cutoff)
                            {
                                o.Files.Add(new FileEntry { Name = fi.Name, FullPath = fi.FullName, Ext = fi.Extension.ToLowerInvariant(), Size = fi.Length, Modified = fi.LastWriteTime });
                                o.Size += fi.Length;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        try { if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue; }
                        catch { continue; }
                        stack.Push(sub);
                    }
                }
                catch { }
            }
        }

        MftIndex GetIndex(string path, Dictionary<string, MftIndex> cache)
        {
            string drive;
            try { drive = Path.GetPathRoot(path); } catch { return null; }
            if (string.IsNullOrEmpty(drive) || drive.StartsWith(@"\\")) return null;  // UNC share: no index
            MftIndex idx;
            if (cache.TryGetValue(drive, out idx)) return idx;
            idx = null;
            try
            {
                if (string.Equals(new DriveInfo(drive).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    idx = new MftReader(() => Cancel, t => Status = "Reading " + t).ReadIndex(drive);
            }
            catch { idx = null; }
            cache[drive] = idx;
            return idx;
        }
    }

    static class OwnerMail
    {
        public static string Subject(OwnerEntry o, int ageDays)
        {
            string folderName = Path.GetFileName(o.Folder);
            if (string.IsNullOrEmpty(folderName)) folderName = o.Folder;
            int months = (int)Math.Round(ageDays / 30.0);
            return string.Format("Please review {0:N0} old files in {1} ({2})", o.Files.Count, folderName, Util.Size(o.Size));
        }

        static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public static string BodyHtml(OwnerEntry o, int ageDays, int maxRows)
        {
            string who = string.IsNullOrEmpty(o.Name) ? "Hello," : "Hello " + Esc(o.Name) + ",";
            int months = (int)Math.Round(ageDays / 30.0);
            var sb = new StringBuilder();
            sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1A2433\">");
            sb.Append("<p>" + who + "</p>");
            sb.AppendFormat("<p>The folder <b>{0}</b> contains <b>{1:N0}</b> files that have not changed in more than {2} months. " +
                "Together they use <b>{3}</b>. Please review the list below and let us know which ones can be removed to free up space.</p>",
                Esc(o.Folder), o.Files.Count, months, Util.Size(o.Size));
            sb.Append("<table style=\"border-collapse:collapse;font-size:13px;margin-top:8px\">");
            sb.Append("<tr style=\"text-align:left;color:#5E6B80\">" +
                      "<th style=\"padding:6px 14px 6px 0\">File</th>" +
                      "<th style=\"padding:6px 14px 6px 0\">Size</th>" +
                      "<th style=\"padding:6px 14px 6px 0\">Last changed</th>" +
                      "<th style=\"padding:6px 0\">Folder</th></tr>");
            int shown = 0;
            foreach (FileEntry f in o.Files.OrderByDescending(x => x.Size))
            {
                if (shown++ >= maxRows) break;
                string folder = Path.GetDirectoryName(f.FullPath) ?? "";
                sb.Append("<tr style=\"border-top:1px solid #DCE1E8\">" +
                    "<td style=\"padding:5px 14px 5px 0\">" + Esc(f.Name) + "</td>" +
                    "<td style=\"padding:5px 14px 5px 0;white-space:nowrap\">" + Util.Size(f.Size) + "</td>" +
                    "<td style=\"padding:5px 14px 5px 0;white-space:nowrap\">" + f.Modified.ToString("yyyy-MM-dd") + "</td>" +
                    "<td style=\"padding:5px 0;color:#5E6B80\">" + Esc(folder) + "</td></tr>");
            }
            sb.Append("</table>");
            if (o.Files.Count > maxRows)
                sb.AppendFormat("<p style=\"color:#5E6B80\">...and {0:N0} more files. The full list is attached as a CSV file.</p>", o.Files.Count - maxRows);
            sb.Append("<p style=\"color:#5E6B80;font-size:12px;margin-top:16px\">This message was generated by Space Analyzer. " +
                      "No files have been deleted; this is only a review request.</p></div>");
            return sb.ToString();
        }

        public static string Csv(OwnerEntry o)
        {
            var sb = new StringBuilder();
            sb.AppendLine("File,Folder,SizeBytes,Size,LastChanged");
            foreach (FileEntry f in o.Files.OrderByDescending(x => x.Size))
                sb.AppendLine(string.Join(",", Util.Csv(f.Name), Util.Csv(Path.GetDirectoryName(f.FullPath) ?? ""),
                    f.Size.ToString(CultureInfo.InvariantCulture), Util.Csv(Util.Size(f.Size)),
                    Util.Csv(f.Modified.ToString("yyyy-MM-dd HH:mm"))));
            return sb.ToString();
        }

        // Builds a MailMessage with the CSV attached
        public static System.Net.Mail.MailMessage Build(OwnerEntry o, int ageDays, string from, bool ccSelf)
        {
            var m = new System.Net.Mail.MailMessage();
            m.From = new System.Net.Mail.MailAddress(from);
            m.To.Add(o.Email);
            if (ccSelf) m.CC.Add(from);
            m.Subject = Subject(o, ageDays);
            m.IsBodyHtml = true;
            m.Body = BodyHtml(o, ageDays, 200);
            byte[] csv = Encoding.UTF8.GetBytes(Csv(o));
            string attName = "OldFiles_" + SafeName(Path.GetFileName(o.Folder)) + ".csv";
            m.Attachments.Add(new System.Net.Mail.Attachment(new MemoryStream(csv), attName, "text/csv"));
            return m;
        }

        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "folder";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        // Writes an .eml file that opens in Outlook
        public static void SaveEml(OwnerEntry o, int ageDays, string from, string dir)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SafeName(o.Email) + "_" + SafeName(Path.GetFileName(o.Folder)) + ".eml");
            var sb = new StringBuilder();
            sb.AppendLine("From: " + from);
            sb.AppendLine("To: " + o.Email);
            sb.AppendLine("Subject: " + Subject(o, ageDays));
            sb.AppendLine("X-Unsent: 1");   // tells Outlook this is a draft to send
            sb.AppendLine("Content-Type: text/html; charset=utf-8");
            sb.AppendLine();
            sb.AppendLine(BodyHtml(o, ageDays, 200));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // ------------------------------------------------------------------
    //  Window: Old files → owners
    // ------------------------------------------------------------------
    class OwnerReportForm : Form
    {
        // Runs an action on the UI thread, but never crashes if the window isn't ready yet.
        void SafeInvoke(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch { }
        }

        List<OwnerEntry> owners = new List<OwnerEntry>();
        DataGridView grid;
        WebBrowser preview;
        Label lblStatus, lblMail;
        NumericUpDown ageBox;
        FlatBtn btnScan, btnStop, btnDrafts, btnSend, btnReload, btnOpenCfg;
        OwnerScanner scanner;
        System.Windows.Forms.Timer timer;
        bool busy;
        int AgeDays { get { return (int)ageBox.Value; } }

        public OwnerReportForm()
        {
            Text = "Old files and their owners";
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            Font = Theme.Body;
            Size = new Size(1160, 760);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            OwnerConfig.EnsureTemplates();

            // ---- header ----
            var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Theme.Bg };
            var title = new Label { Text = "Old files and their owners", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(18, 14) };
            var sub = new Label
            {
                Text = "Finds files not changed for a while in the folders you list, then emails each folder's owner a review request. Nothing is deleted.",
                Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false, Size = new Size(760, 38), Location = new Point(20, 46)
            };
            var ageLbl = new Label { Text = "Older than", AutoSize = true, Location = new Point(22, 78), ForeColor = Theme.Ink, Font = Theme.Body };
            ageBox = new NumericUpDown { Minimum = 1, Maximum = 3650, Value = 180, Increment = 30, Location = new Point(98, 75), Width = 64, Font = Theme.Body };
            var ageUnit = new Label { Text = "days  (180 = about 6 months)", AutoSize = true, Location = new Point(168, 78), ForeColor = Theme.Muted, Font = Theme.Body };
            header.Controls.Add(title);
            header.Controls.Add(sub);
            header.Controls.Add(ageLbl);
            header.Controls.Add(ageBox);
            header.Controls.Add(ageUnit);

            btnReload = new FlatBtn("Reload list", BtnKind.Secondary);
            btnOpenCfg = new FlatBtn("Edit folder list", BtnKind.Secondary);
            btnScan = new FlatBtn("Find old files", BtnKind.Primary);
            btnStop = new FlatBtn("Stop", BtnKind.Secondary);
            btnReload.Click += (s, e) => LoadOwners();
            btnOpenCfg.Click += (s, e) => OpenConfig();
            btnScan.Click += (s, e) => StartScan();
            btnStop.Click += (s, e) => { if (scanner != null) scanner.Cancel = true; };
            var top = RightFlow(header, 20, btnScan, btnStop, btnOpenCfg, btnReload);
            header.Controls.Add(top);
            header.Resize += (s, e) => { sub.Width = Math.Max(300, header.Width - top.Width - 40); top.Left = header.Width - top.Width; };

            // ---- split: owners list | email preview ----
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 14, BackColor = Theme.Bg };
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;
            split.Panel1MinSize = 360;

            var listCard = new Card { Title = "Folders and owners", Subtitle = "From OwnerMap.csv. Click a row to preview its email." };
            grid = GridStyle.Make();
            grid.MultiSelect = false;
            grid.Columns.Add(GridStyle.Col("folder", "Folder", 0, 46, false));
            grid.Columns.Add(GridStyle.Col("email", "Owner", 0, 34, false));
            grid.Columns.Add(GridStyle.Col("old", "Old files", 80, 0, true));
            grid.Columns.Add(GridStyle.Col("size", "Size", 90, 0, true));
            grid.Columns.Add(GridStyle.Col("status", "Status", 0, 40, false));
            grid.SelectionChanged += (s, e) => ShowPreview();
            listCard.Controls.Add(grid);
            split.Panel1.Controls.Add(listCard);

            var prevCard = new Card { Title = "Email preview", Subtitle = "What the selected owner will receive" };
            preview = new WebBrowser { Dock = DockStyle.Fill, IsWebBrowserContextMenuEnabled = false, AllowWebBrowserDrop = false, WebBrowserShortcutsEnabled = false };
            prevCard.Controls.Add(preview);
            split.Panel2.Controls.Add(prevCard);

            // ---- footer ----
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Theme.Bg };
            lblStatus = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Small, Padding = new Padding(20, 2, 0, 0), AutoEllipsis = true };
            lblMail = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Small, Padding = new Padding(20, 0, 0, 0), AutoEllipsis = true };
            btnDrafts = new FlatBtn("Save drafts (.eml)", BtnKind.Secondary);
            btnSend = new FlatBtn("Send emails", BtnKind.Primary);
            btnDrafts.Click += (s, e) => SaveDrafts();
            btnSend.Click += (s, e) => SendEmails();
            var fbtns = RightFlow(footer, 12, btnSend, btnDrafts);
            footer.Controls.Add(fbtns);
            footer.Controls.Add(lblMail);
            footer.Controls.Add(lblStatus);
            footer.Resize += (s, e) => fbtns.Left = footer.Width - fbtns.Width;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0), BackColor = Theme.Bg };
            body.Controls.Add(split);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && !busy) Close(); };
            timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (s, e) => { if (scanner != null) lblStatus.Text = scanner.Status.Length > 0 ? scanner.Status : lblStatus.Text; };
            Shown += (s, e) => { LoadOwners(); ShowMailState(); };
        }

        FlowLayoutPanel RightFlow(Control parent, int top, params Control[] ctrls)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, Height = 46, Top = top };
            int w = 8;
            foreach (Control c in ctrls) { f.Controls.Add(c); w += c.Width + c.Margin.Horizontal; }
            f.Width = w;
            f.Left = parent.Width - w;
            f.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            return f;
        }

        void LoadOwners()
        {
            string err;
            owners = OwnerConfig.LoadMap(out err);
            grid.Rows.Clear();
            foreach (OwnerEntry o in owners)
            {
                int i = grid.Rows.Add(o.Folder, string.IsNullOrEmpty(o.Name) ? o.Email : o.Name + "  <" + o.Email + ">", "", "", "Not checked yet");
                grid.Rows[i].Tag = o;
            }
            grid.ClearSelection();
            preview.DocumentText = "<body style='font-family:Segoe UI;color:#5E6B80;padding:20px'>Select a folder after finding old files to preview its email.</body>";
            if (err != null) lblStatus.Text = "Could not read OwnerMap.csv: " + err;
            else if (owners.Count == 0) lblStatus.Text = "OwnerMap.csv has no folders yet. Click \"Edit folder list\" to add some.";
            else lblStatus.Text = owners.Count + " folder(s) loaded. Set the age and click \"Find old files\".";
            UpdateButtons();
        }

        void OpenConfig()
        {
            try { Process.Start(OwnerConfig.MapPath); }
            catch { Util.OpenInExplorer(OwnerConfig.MapPath, true); }
        }

        void StartScan()
        {
            if (busy || owners.Count == 0) return;
            busy = true;
            scanner = new OwnerScanner(AgeDays);
            OwnerScanner sc = scanner;
            UpdateButtons();
            lblStatus.Text = "Looking for files older than " + AgeDays + " days...";
            var t = new Thread(() =>
            {
                try { sc.Scan(owners); } catch { }
                try { SafeInvoke((Action)(() => ScanDone(sc))); } catch { }
            });
            t.IsBackground = true;
            t.Start();
            timer.Start();
        }

        void ScanDone(OwnerScanner sc)
        {
            timer.Stop();
            busy = false;
            foreach (DataGridViewRow r in grid.Rows)
            {
                OwnerEntry o = r.Tag as OwnerEntry;
                if (o == null) continue;
                r.Cells["old"].Value = o.Files.Count > 0 ? o.Files.Count.ToString("N0") : "";
                r.Cells["size"].Value = o.Size > 0 ? Util.Size(o.Size) : "";
                r.Cells["status"].Value = o.Status;
            }
            int withFiles = owners.Count(o => o.Files.Count > 0);
            long total = owners.Sum(o => o.Size);
            lblStatus.Text = sc.Cancel ? "Stopped. Showing what was found so far."
                : string.Format("Done. {0} of {1} folder(s) have old files, {2} in total.", withFiles, owners.Count, Util.Size(total));
            if (grid.Rows.Count > 0 && grid.SelectedRows.Count == 0) grid.Rows[0].Selected = true;
            ShowPreview();
            UpdateButtons();
        }

        OwnerEntry Selected
        {
            get { return grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as OwnerEntry : null; }
        }

        void ShowPreview()
        {
            OwnerEntry o = Selected;
            if (o == null || o.Files.Count == 0)
            {
                preview.DocumentText = "<body style='font-family:Segoe UI;color:#5E6B80;padding:20px'>" +
                    (o == null ? "Select a folder." : "No files older than " + AgeDays + " days in this folder, so no email is needed.") + "</body>";
                return;
            }
            preview.DocumentText =
                "<div style='font-family:Segoe UI;font-size:13px;padding:14px 18px'>" +
                "<div style='color:#5E6B80'><b>To:</b> " + o.Email + "</div>" +
                "<div style='color:#5E6B80;margin-bottom:10px'><b>Subject:</b> " + OwnerMail.Subject(o, AgeDays) + "</div><hr style='border:none;border-top:1px solid #DCE1E8'>" +
                OwnerMail.BodyHtml(o, AgeDays, 200) + "</div>";
        }

        List<OwnerEntry> Mailable { get { return owners.Where(o => o.Files.Count > 0).ToList(); } }

        void SaveDrafts()
        {
            List<OwnerEntry> list = Mailable;
            if (list.Count == 0) { MessageBox.Show(this, "No old files were found, so there is nothing to send. Run \"Find old files\" first.", "Save drafts", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var cfg = OwnerConfig.LoadEmail();
            string from = cfg.ContainsKey("From") ? cfg["From"] : "spacereports@example.com";
            string dir = Path.Combine(OwnerConfig.DraftsDir, DateTime.Now.ToString("yyyyMMdd_HHmm"));
            int n = 0;
            foreach (OwnerEntry o in list) { try { OwnerMail.SaveEml(o, AgeDays, from, dir); n++; } catch { } }
            lblStatus.Text = string.Format("Saved {0} draft(s) to {1}", n, dir);
            Util.OpenInExplorer(dir, false);
            MessageBox.Show(this, string.Format("Saved {0} draft email(s).\n\nOpen a .eml file to review it in Outlook, then click Send there. Double-check the recipients before sending.", n),
                "Drafts saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void SendEmails()
        {
            List<OwnerEntry> list = Mailable;
            if (list.Count == 0) { MessageBox.Show(this, "No old files were found, so there is nothing to send.", "Send emails", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var cfg = OwnerConfig.LoadEmail();
            string server = cfg.ContainsKey("SmtpServer") ? cfg["SmtpServer"] : "";
            string from = cfg.ContainsKey("From") ? cfg["From"] : "";
            if (server.Length == 0 || from.Length == 0 || from.IndexOf('@') < 0)
            {
                MessageBox.Show(this, "Email is not set up yet. Open EmailSettings.ini (next to the app) and fill in SmtpServer and From.\n\n" +
                    "If you can't use SMTP, use \"Save drafts\" instead and send the .eml files from Outlook.",
                    "Email not configured", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var recipients = list.Select(o => o.Email).Distinct().ToList();
            if (MessageBox.Show(this,
                    string.Format("Send {0} email(s) to {1} recipient(s) now?\n\nFrom: {2}\nServer: {3}\n\nEach person gets the list of old files in their folder.",
                        list.Count, recipients.Count, from, server),
                    "Confirm send", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            int port = 25; int.TryParse(cfg.ContainsKey("Port") ? cfg["Port"] : "25", out port);
            bool ssl = cfg.ContainsKey("UseSsl") && cfg["UseSsl"].Equals("true", StringComparison.OrdinalIgnoreCase);
            bool ccSelf = cfg.ContainsKey("CcToSelf") && cfg["CcToSelf"].Equals("true", StringComparison.OrdinalIgnoreCase);
            string user = cfg.ContainsKey("Username") ? cfg["Username"] : "";
            string pass = cfg.ContainsKey("Password") ? cfg["Password"] : "";

            busy = true; UpdateButtons();
            int ok = 0; var errors = new List<string>();
            try
            {
                using (var smtp = new System.Net.Mail.SmtpClient(server, port))
                {
                    smtp.EnableSsl = ssl;
                    smtp.Timeout = 30000;
                    if (user.Length > 0) smtp.Credentials = new System.Net.NetworkCredential(user, pass);
                    foreach (OwnerEntry o in list)
                    {
                        lblStatus.Text = "Sending to " + o.Email + "...";
                        Application.DoEvents();
                        try { using (var m = OwnerMail.Build(o, AgeDays, from, ccSelf)) smtp.Send(m); ok++; }
                        catch (Exception ex) { errors.Add(o.Email + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            busy = false; UpdateButtons();
            lblStatus.Text = string.Format("Sent {0} of {1} email(s).", ok, list.Count);
            string msg = string.Format("Sent {0} of {1} email(s).", ok, list.Count);
            if (errors.Count > 0) msg += "\n\nProblems:\n" + string.Join("\n", errors.Take(10));
            MessageBox.Show(this, msg, "Send emails", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        void ShowMailState()
        {
            var cfg = OwnerConfig.LoadEmail();
            string server = cfg.ContainsKey("SmtpServer") ? cfg["SmtpServer"] : "";
            lblMail.Text = server.Length > 0
                ? "Email server: " + server + "   (edit EmailSettings.ini to change)"
                : "Email server not set. You can still use Save drafts. To send directly, fill in EmailSettings.ini next to the app.";
        }

        void UpdateButtons()
        {
            bool has = owners.Count > 0;
            btnScan.Enabled = !busy && has;
            btnStop.Enabled = busy;
            btnReload.Enabled = !busy;
            btnOpenCfg.Enabled = !busy;
            bool any = !busy && Mailable.Count > 0;
            btnDrafts.Enabled = any;
            btnSend.Enabled = any;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (scanner != null) scanner.Cancel = true;
            base.OnFormClosing(e);
        }
    }

    // ==================================================================
    //  AZURE: sign in, list the VMs you can access, power them on/off,
    //  and open Remote Desktop so the full app can run on the VM.
    //  Uses only built-in .NET pieces (HttpClient + JavaScriptSerializer).
    // ==================================================================
    class AzureVm
    {
        public string Name, Id, ResourceGroup, Location, Size, Subscription, SubName, Power = "unknown";
        public string NicId;
    }

    class AzureClient
    {
        // The Azure CLI's public app id. Using it means you don't have to register
        // an app in Azure; you just sign in the same way "az login" does.
        const string ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
        const string Authority = "https://login.microsoftonline.com/organizations/oauth2/v2.0";
        const string Scope = "https://management.azure.com/.default offline_access openid profile";
        const string Arm = "https://management.azure.com";

        readonly System.Net.Http.HttpClient http = new System.Net.Http.HttpClient();
        readonly System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer();
        string token;

        public AzureClient()
        {
            http.Timeout = TimeSpan.FromSeconds(100);
            http.DefaultRequestHeaders.Add("User-Agent", "SpaceAnalyzer/1.0");
            js.MaxJsonLength = 64 * 1024 * 1024;
            try
            {
                System.Net.IWebProxy proxy = System.Net.WebRequest.DefaultWebProxy;
                if (proxy != null) proxy.Credentials = System.Net.CredentialCache.DefaultCredentials;
            }
            catch { }
        }

        // ---------- tiny JSON helpers ----------
        Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        object[] Arr(object o) { return o as object[]; }
        string Str(object o, params string[] path)
        {
            foreach (string key in path)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out o)) return null;
            }
            return o == null ? null : o.ToString();
        }
        object Node(object o, params string[] path)
        {
            foreach (string key in path)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out o)) return null;
            }
            return o;
        }

        string Form(Dictionary<string, string> kv)
        {
            return string.Join("&", kv.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }

        Dictionary<string, object> PostForm(string url, Dictionary<string, string> body)
        {
            var content = new System.Net.Http.StringContent(Form(body), Encoding.UTF8, "application/x-www-form-urlencoded");
            try
            {
                var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
                string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return Obj(js.DeserializeObject(text));
            }
            catch (Exception ex) { throw Explain(ex); }
        }

        object Get(string url)
        {
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.Add("Authorization", "Bearer " + token);
            System.Net.Http.HttpResponseMessage resp;
            string text;
            try
            {
                resp = http.SendAsync(req).GetAwaiter().GetResult();
                text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) { throw Explain(ex); }
            if (!resp.IsSuccessStatusCode) throw new Exception("Azure said: " + resp.StatusCode + " - " + Short(text));
            return js.DeserializeObject(text);
        }

        void Post(string url)
        {
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url);
            req.Headers.Add("Authorization", "Bearer " + token);
            req.Content = new System.Net.Http.StringContent("", Encoding.UTF8, "application/json");
            System.Net.Http.HttpResponseMessage resp;
            try { resp = http.SendAsync(req).GetAwaiter().GetResult(); }
            catch (Exception ex) { throw Explain(ex); }
            if (!resp.IsSuccessStatusCode)
                throw new Exception("Azure said: " + resp.StatusCode + " - " + Short(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
        }

        // Unwraps nested errors and gives a plain-language hint for the common ones.
        static Exception Explain(Exception ex)
        {
            Exception e = ex;
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            while (e.InnerException != null && (e is System.Net.Http.HttpRequestException)) e = e.InnerException;
            string m = e.Message;
            if (m.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("secure channel", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Exception("Secure connection failed. This VM may need TLS 1.2 enabled or a Windows update. (" + m + ")");
            if (m.IndexOf("proxy", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("407", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Exception("A network proxy is blocking the connection. Ask your network team to allow login.microsoftonline.com and management.azure.com. (" + m + ")");
            if (m.IndexOf("remote name", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("resolve", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Exception("Could not reach the internet from this machine (DNS/connection). (" + m + ")");
            if (m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 || e is System.Threading.Tasks.TaskCanceledException)
                return new Exception("The connection timed out. Check this machine's internet access or proxy. (" + m + ")");
            return e;
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(no detail)";
            return s.Length > 300 ? s.Substring(0, 300) : s;
        }

        // ---------- sign in (device code, same as az login) ----------
        public class DeviceCode { public string UserCode, Url, Message, DeviceCodeValue; public int Interval, Expires; }

        public DeviceCode BeginSignIn()
        {
            var r = PostForm(Authority + "/devicecode", new Dictionary<string, string> {
                { "client_id", ClientId }, { "scope", Scope } });
            if (r == null || !r.ContainsKey("user_code")) throw new Exception("Could not start sign-in. Your tenant may block device-code login.");
            return new DeviceCode
            {
                UserCode = Str(r, "user_code"),
                Url = Str(r, "verification_uri"),
                Message = Str(r, "message"),
                DeviceCodeValue = Str(r, "device_code"),
                Interval = int.Parse(Str(r, "interval") ?? "5"),
                Expires = int.Parse(Str(r, "expires_in") ?? "900")
            };
        }

        // Waits for you to finish signing in in the browser. Returns true on success.
        public bool CompleteSignIn(DeviceCode dc, Func<bool> cancel)
        {
            DateTime stop = DateTime.Now.AddSeconds(dc.Expires);
            int interval = Math.Max(3, dc.Interval);
            while (DateTime.Now < stop)
            {
                if (cancel()) return false;
                Thread.Sleep(interval * 1000);
                var r = PostForm(Authority + "/token", new Dictionary<string, string> {
                    { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" },
                    { "client_id", ClientId }, { "device_code", dc.DeviceCodeValue } });
                if (r == null) continue;
                if (r.ContainsKey("access_token")) { token = Str(r, "access_token"); return true; }
                string err = Str(r, "error");
                if (err == "authorization_pending" || err == "slow_down") continue;
                throw new Exception(Str(r, "error_description") ?? err ?? "Sign-in failed.");
            }
            return false;
        }

        public bool SignedIn { get { return !string.IsNullOrEmpty(token); } }

        // ---------- VMs ----------
        public List<AzureVm> ListVms(Action<string> status)
        {
            var vms = new List<AzureVm>();
            object subs = Get(Arm + "/subscriptions?api-version=2020-01-01");
            object[] subArr = Arr(Node(subs, "value")) ?? new object[0];
            foreach (object sub in subArr)
            {
                string subId = Str(sub, "subscriptionId");
                string subName = Str(sub, "displayName");
                if (subId == null) continue;
                status("Reading VMs in " + subName + "...");
                object list;
                try { list = Get(Arm + "/subscriptions/" + subId + "/providers/Microsoft.Compute/virtualMachines?api-version=2023-07-01"); }
                catch { continue; }
                foreach (object vm in Arr(Node(list, "value")) ?? new object[0])
                {
                    string id = Str(vm, "id");
                    if (id == null) continue;
                    var v = new AzureVm
                    {
                        Name = Str(vm, "name"),
                        Id = id,
                        Location = Str(vm, "location"),
                        Size = Str(vm, "properties", "hardwareProfile", "vmSize"),
                        Subscription = subId,
                        SubName = subName,
                        ResourceGroup = GroupFromId(id)
                    };
                    object nics = Node(vm, "properties", "networkProfile", "networkInterfaces");
                    object[] nicArr = Arr(nics);
                    if (nicArr != null && nicArr.Length > 0) v.NicId = Str(nicArr[0], "id");
                    vms.Add(v);
                }
            }
            return vms;
        }

        public void RefreshPower(AzureVm v)
        {
            try
            {
                object iv = Get(Arm + v.Id + "/instanceView?api-version=2023-07-01");
                foreach (object st in Arr(Node(iv, "statuses")) ?? new object[0])
                {
                    string code = Str(st, "code");
                    if (code != null && code.StartsWith("PowerState/"))
                    {
                        v.Power = code.Substring("PowerState/".Length);
                        return;
                    }
                }
                v.Power = "unknown";
            }
            catch { v.Power = "unknown"; }
        }

        public void Start(AzureVm v) { Post(Arm + v.Id + "/start?api-version=2023-07-01"); }
        public void Deallocate(AzureVm v) { Post(Arm + v.Id + "/deallocate?api-version=2023-07-01"); }

        // Best-effort address for Remote Desktop: public IP/DNS if there is one, else private IP.
        public string GetRdpAddress(AzureVm v)
        {
            if (string.IsNullOrEmpty(v.NicId)) return null;
            object nic = Get(Arm + v.NicId + "?api-version=2023-07-01");
            object ipcfg = (Arr(Node(nic, "properties", "ipConfigurations")) ?? new object[0]).FirstOrDefault();
            if (ipcfg == null) return null;
            string priv = Str(ipcfg, "properties", "privateIPAddress");
            string pubId = Str(ipcfg, "properties", "publicIPAddress", "id");
            if (pubId != null)
            {
                try
                {
                    object pub = Get(Arm + pubId + "?api-version=2023-07-01");
                    string fqdn = Str(pub, "properties", "dnsSettings", "fqdn");
                    string ip = Str(pub, "properties", "ipAddress");
                    if (!string.IsNullOrEmpty(fqdn)) return fqdn;
                    if (!string.IsNullOrEmpty(ip)) return ip;
                }
                catch { }
            }
            return priv;
        }

        static string GroupFromId(string id)
        {
            int i = id.IndexOf("/resourceGroups/", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int start = i + "/resourceGroups/".Length;
            int end = id.IndexOf('/', start);
            return end < 0 ? id.Substring(start) : id.Substring(start, end - start);
        }
    }

    // ------------------------------------------------------------------
    //  Window: Azure VMs
    // ------------------------------------------------------------------
    class AzureForm : Form
    {
        // Runs an action on the UI thread, but never crashes if the window isn't ready yet.
        void SafeInvoke(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch { }
        }

        readonly AzureClient az = new AzureClient();
        List<AzureVm> vms = new List<AzureVm>();
        DataGridView grid;
        Label lblStatus;
        FlatBtn btnConnect, btnRefresh, btnStart, btnStop, btnRdp;
        System.Windows.Forms.Timer timer;
        AzureClient.DeviceCode pending;
        volatile bool cancelLogin, busy;
        volatile string work = "";

        public AzureForm()
        {
            Text = "Azure VMs";
            BackColor = Theme.Bg; ForeColor = Theme.Ink; Font = Theme.Body;
            Size = new Size(1120, 680);
            MinimumSize = new Size(880, 520);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Bg };
            var title = new Label { Text = "Azure VMs", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(18, 14) };
            var sub = new Label
            {
                Text = "Sign in with your Azure account to see the VMs you can access, power them on or off, and open Remote Desktop. " +
                       "The full scan-and-clean experience runs on the VM itself.",
                Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false, Size = new Size(760, 38), Location = new Point(20, 46)
            };
            header.Controls.Add(title); header.Controls.Add(sub);

            btnConnect = new FlatBtn("Sign in to Azure", BtnKind.Primary); btnConnect.Width = 150;
            btnRefresh = new FlatBtn("Refresh", BtnKind.Secondary);
            btnStart = new FlatBtn("Start", BtnKind.Secondary);
            btnStop = new FlatBtn("Stop (deallocate)", BtnKind.Secondary);
            btnRdp = new FlatBtn("Open Remote Desktop", BtnKind.Primary); btnRdp.Width = 180;
            btnConnect.Click += (s, e) => SignIn();
            btnRefresh.Click += (s, e) => LoadVms();
            btnStart.Click += (s, e) => Power(true);
            btnStop.Click += (s, e) => Power(false);
            btnRdp.Click += (s, e) => OpenRdp();
            var top = RightFlow(header, 22, btnConnect, btnRefresh);
            header.Controls.Add(top);
            header.Resize += (s, e) => { sub.Width = Math.Max(300, header.Width - top.Width - 40); top.Left = header.Width - top.Width; };

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0), BackColor = Theme.Bg };
            var card = new Card { Title = "Virtual machines", Subtitle = "Select a VM, then use the buttons below" };
            grid = GridStyle.Make();
            grid.MultiSelect = false;
            grid.Columns.Add(GridStyle.Col("name", "Name", 0, 32, false));
            grid.Columns.Add(GridStyle.Col("power", "Power", 120, 0, false));
            grid.Columns.Add(GridStyle.Col("rg", "Resource group", 0, 28, false));
            grid.Columns.Add(GridStyle.Col("size", "Size", 150, 0, false));
            grid.Columns.Add(GridStyle.Col("loc", "Location", 110, 0, false));
            grid.Columns.Add(GridStyle.Col("sub", "Subscription", 0, 28, false));
            grid.CellPainting += PaintPower;
            grid.SelectionChanged += (s, e) => UpdateButtons();
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) OpenRdp(); };
            card.Controls.Add(grid);
            body.Controls.Add(card);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg };
            lblStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 0, 0, 0), ForeColor = Theme.Muted, Font = Theme.Small, AutoEllipsis = true, Text = "Not signed in." };
            var fbtns = RightFlow(footer, 14, btnRdp, btnStop, btnStart);
            footer.Controls.Add(lblStatus); footer.Controls.Add(fbtns);
            footer.Resize += (s, e) => fbtns.Left = footer.Width - fbtns.Width;

            Controls.Add(body); Controls.Add(footer); Controls.Add(header);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && !busy) Close(); };
            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (s, e) => { if (work.Length > 0) lblStatus.Text = work; };
            UpdateButtons();
        }

        FlowLayoutPanel RightFlow(Control parent, int top, params Control[] ctrls)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, Height = 46, Top = top };
            int w = 8;
            foreach (Control c in ctrls) { f.Controls.Add(c); w += c.Width + c.Margin.Horizontal; }
            f.Width = w; f.Left = parent.Width - w; f.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            return f;
        }

        void PaintPower(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "power") return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);
            string val = Convert.ToString(e.Value) ?? "";
            Color dot = val.IndexOf("running", StringComparison.OrdinalIgnoreCase) >= 0 ? Color.FromArgb(30, 122, 80)
                      : val.IndexOf("dealloc", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.Faint
                      : val.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.Warn : Theme.Gray;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.CellBounds;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, r.X + 10, r.Y + r.Height / 2f - 5, 10, 10);
            TextRenderer.DrawText(g, val, Theme.Body, new Rectangle(r.X + 28, r.Y, r.Width - 32, r.Height), Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            e.Handled = true;
        }

        void SignIn()
        {
            if (busy) return;
            busy = true; cancelLogin = false; work = "Starting sign-in...";
            UpdateButtons(); timer.Start();
            new Thread(() =>
            {
                try
                {
                    pending = az.BeginSignIn();
                    try { Clipboard.SetText(pending.UserCode); } catch { }
                    try { Process.Start(pending.Url); } catch { }
                    SafeInvoke((Action)(() => ShowCode(pending)));
                    bool ok = az.CompleteSignIn(pending, () => cancelLogin);
                    SafeInvoke((Action)(() => { busy = false; if (ok) { work = "Signed in."; LoadVms(); } else { work = "Sign-in cancelled or timed out."; timer.Stop(); UpdateButtons(); } }));
                }
                catch (Exception ex)
                {
                    Exception e = ex;
                    while (e is AggregateException && e.InnerException != null) e = e.InnerException;
                    string msg = e.Message;
                    SafeInvoke((Action)(() => { busy = false; timer.Stop(); lblStatus.Text = "Sign-in failed: " + msg; UpdateButtons(); }));
                }
            }) { IsBackground = true }.Start();
        }

        void ShowCode(AzureClient.DeviceCode dc)
        {
            MessageBox.Show(this,
                "A browser has opened for Azure sign-in.\n\n" +
                "Enter this code (already copied to your clipboard):\n\n        " + dc.UserCode + "\n\n" +
                "If the browser didn't open, go to:\n" + dc.Url + "\n\n" +
                "After you finish signing in, this window will fill with your VMs.",
                "Sign in to Azure", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void LoadVms()
        {
            if (!az.SignedIn) return;
            busy = true; work = "Loading VMs..."; UpdateButtons(); timer.Start();
            new Thread(() =>
            {
                string err = null;
                List<AzureVm> list = null;
                try
                {
                    list = az.ListVms(s => work = s);
                    foreach (AzureVm v in list) { if (cancelLogin) break; work = "Checking power state of " + v.Name + "..."; az.RefreshPower(v); }
                }
                catch (Exception ex) { err = ex.Message; }
                SafeInvoke((Action)(() =>
                {
                    busy = false; timer.Stop();
                    if (err != null) { lblStatus.Text = "Could not load VMs: " + err; UpdateButtons(); return; }
                    vms = list;
                    Fill();
                    lblStatus.Text = string.Format("{0} VM(s) you can access.", vms.Count);
                    UpdateButtons();
                }));
            }) { IsBackground = true }.Start();
        }

        void Fill()
        {
            grid.Rows.Clear();
            foreach (AzureVm v in vms.OrderBy(x => x.SubName).ThenBy(x => x.Name))
            {
                int i = grid.Rows.Add(v.Name, v.Power, v.ResourceGroup, v.Size, v.Location, v.SubName);
                grid.Rows[i].Tag = v;
            }
            grid.ClearSelection();
        }

        AzureVm Selected { get { return grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as AzureVm : null; } }

        void Power(bool start)
        {
            AzureVm v = Selected;
            if (v == null || busy) return;
            if (!start && MessageBox.Show(this,
                    "Deallocate " + v.Name + "?\n\nThis shuts the VM down and stops charges for it. You can start it again any time.",
                    "Stop VM", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            busy = true; work = (start ? "Starting " : "Stopping ") + v.Name + "..."; UpdateButtons(); timer.Start();
            new Thread(() =>
            {
                string err = null;
                try { if (start) az.Start(v); else az.Deallocate(v); Thread.Sleep(3000); az.RefreshPower(v); }
                catch (Exception ex) { err = ex.Message; }
                SafeInvoke((Action)(() =>
                {
                    busy = false; timer.Stop();
                    lblStatus.Text = err != null ? "Failed: " + err
                        : v.Name + " is now " + v.Power + ". (It can take a minute to finish; click Refresh.)";
                    Fill(); UpdateButtons();
                }));
            }) { IsBackground = true }.Start();
        }

        void OpenRdp()
        {
            AzureVm v = Selected;
            if (v == null || busy) return;
            if (v.Power.IndexOf("running", StringComparison.OrdinalIgnoreCase) < 0 &&
                MessageBox.Show(this, v.Name + " doesn't look like it's running. Open Remote Desktop anyway?",
                    "Open Remote Desktop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            busy = true; work = "Finding the address for " + v.Name + "..."; UpdateButtons(); timer.Start();
            new Thread(() =>
            {
                string addr = null, err = null;
                try { addr = az.GetRdpAddress(v); } catch (Exception ex) { err = ex.Message; }
                SafeInvoke((Action)(() =>
                {
                    busy = false; timer.Stop(); UpdateButtons();
                    if (string.IsNullOrEmpty(addr))
                    {
                        lblStatus.Text = "Could not find an address for this VM" + (err != null ? ": " + err : ". It may have no reachable IP.");
                        return;
                    }
                    try { Process.Start("mstsc.exe", "/v:" + addr); lblStatus.Text = "Opening Remote Desktop to " + addr + ". Sign in, then run SpaceAnalyzer.exe on the VM."; }
                    catch (Exception ex) { lblStatus.Text = "Could not start Remote Desktop: " + ex.Message; }
                }));
            }) { IsBackground = true }.Start();
        }

        void UpdateButtons()
        {
            bool signed = az.SignedIn && !busy;
            btnConnect.Enabled = !busy;
            btnConnect.Text = az.SignedIn ? "Signed in" : "Sign in to Azure";
            btnRefresh.Enabled = signed;
            bool sel = signed && Selected != null;
            btnStart.Enabled = sel; btnStop.Enabled = sel; btnRdp.Enabled = sel;
        }
    }

    // ==================================================================
    //  DEPLOY-AND-RUN: copy the app to a VM, run a fast scan there over
    //  WMI, and bring the results back. No command prompt; all from the UI.
    // ==================================================================
    class AdvRow { public int Id; public int Level, Kind; public long Size, Count; public string Title; public List<FileHit> Files = new List<FileHit>(); }

    class DriveMeta { public string Letter; public long Total, Free; }

    class RemoteResult
    {
        public string Host;
        public List<DriveMeta> Drives = new List<DriveMeta>();
        public List<string> IniPaths = new List<string>();
        public DirNode Root;
        public List<FileEntry> TopFiles = new List<FileEntry>();
        public Dictionary<string, ExtStat> Exts = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        public List<AdvRow> Adv = new List<AdvRow>();
        public long Files, Bytes;
        public double Seconds;
        public int FastDrives, SlowDrives;
    }

    static class IniFinder
    {
        static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "$Recycle.Bin", "System Volume Information", "WinSxS" };

        public static List<string> FindFixedDrives()
        {
            var roots = new List<string>();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try { if (drive.IsReady && drive.DriveType == DriveType.Fixed) roots.Add(drive.RootDirectory.FullName); }
                catch { }
            }
            return Find(roots);
        }

        public static List<string> Find(IEnumerable<string> roots)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string dir = pending.Pop();
                    try
                    {
                        foreach (string file in Directory.GetFiles(dir, "smartplantv4.ini", SearchOption.TopDirectoryOnly))
                            found.Add(file);
                    }
                    catch { }
                    try
                    {
                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            string name = Path.GetFileName(sub.TrimEnd('\\'));
                            if (SkipNames.Contains(name)) continue;
                            try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub); }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            return found.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    static class RemoteAgent
    {
        public const int TimeoutMinutes = 30;

        public static RemoteResult Run(string host, List<string> driveLetters, Action<string> status, Func<bool> cancel)
        {
            string uncTemp = "\\\\" + host + "\\C$\\Windows\\Temp";
            string agentUnc = uncTemp + "\\SpaceAnalyzerAgent.exe";
            string resultUnc = uncTemp + "\\SA_agent_result.txt";
            string resultLocal = "C:\\Windows\\Temp\\SA_agent_result.txt";
            string logUnc = resultUnc + ".log";

            Log.Line("REMOTE scan of " + host + " drives=" + string.Join(",", driveLetters));
            status("Copying Space Analyzer to " + host + "...");
            try { if (!Directory.Exists(uncTemp)) Directory.CreateDirectory(uncTemp); }
            catch (Exception ex) { Log.Line("  create temp failed: " + ex.Message); }
            try { File.Copy(Application.ExecutablePath, agentUnc, true); Log.Line("  copied agent to " + agentUnc); }
            catch (Exception ex) { Log.Line("  COPY FAILED: " + ex.Message); throw new Exception("Could not copy the app to " + host + " (are you admin on it, and is C$ reachable?): " + ex.Message); }
            try { if (File.Exists(resultUnc)) File.Delete(resultUnc); } catch { }
            try { if (File.Exists(resultUnc + ".err")) File.Delete(resultUnc + ".err"); } catch { }
            try { if (File.Exists(logUnc)) File.Delete(logUnc); } catch { }

            string drives = string.Join(",", driveLetters.Select(d => d.TrimEnd('\\', '$', ':')));
            string cmd = "\"C:\\Windows\\Temp\\SpaceAnalyzerAgent.exe\" /agent \"" + resultLocal + "\" /drives " + drives;

            status("Starting the scan on " + host + "...");
            Log.Line("  WMI launch: " + cmd);
            uint pid = WmiCreate(host, cmd);
            Log.Line("  WMI started pid=" + pid);

            status("Scanning " + host + " on the VM (fast)...");
            DateTime stop = DateTime.Now.AddMinutes(TimeoutMinutes);
            // Grace window: after the process exits, keep looking for the result file,
            // because writing a large report takes a few seconds after the scan ends.
            DateTime? processGoneSince = null;
            const int GraceSeconds = 60;
            while (DateTime.Now < stop)
            {
                if (cancel()) throw new OperationCanceledException();
                if (File.Exists(resultUnc)) break;
                if (File.Exists(resultUnc + ".err")) throw new Exception("The VM reported: " + SafeRead(resultUnc + ".err"));

                if (WmiAlive(host, pid)) { processGoneSince = null; }
                else if (processGoneSince == null) { processGoneSince = DateTime.Now; }
                else if ((DateTime.Now - processGoneSince.Value).TotalSeconds > GraceSeconds)
                {
                    if (File.Exists(resultUnc)) break;
                    if (File.Exists(resultUnc + ".err")) throw new Exception("The VM reported: " + SafeRead(resultUnc + ".err"));
                    string tail = Tail(logUnc);
                    Log.Line("  process ended, no result within grace. Agent log tail:\r\n" + tail);
                    throw new Exception("The scan ran on " + host + " but the result never arrived.\r\n\r\nLast lines from the VM's log:\r\n" + tail +
                        "\r\n(The full log is SpaceAnalyzer_Debug.log next to this app, and SA_agent_result.txt.log in C:\\Windows\\Temp on the VM.)");
                }
                Thread.Sleep(1200);
            }
            if (!File.Exists(resultUnc)) throw new Exception("Timed out waiting for " + host + " to finish scanning.");

            status("Bringing the results back from " + host + "...");
            WaitUntilStable(resultUnc, cancel);
            string text = File.ReadAllText(resultUnc);
            Log.Line("  result received, " + text.Length + " chars");
            try { File.Delete(resultUnc); } catch { }
            return Parse(host, text);
        }

        static string SafeRead(string p) { try { return File.ReadAllText(p); } catch { return "unknown error"; } }

        // Make sure the file is fully written (size stops changing and it opens for read).
        static void WaitUntilStable(string path, Func<bool> cancel)
        {
            long last = -1;
            for (int i = 0; i < 40; i++)
            {
                if (cancel != null && cancel()) return;
                long len = -1;
                try { len = new FileInfo(path).Length; } catch { }
                if (len > 0 && len == last)
                {
                    try { using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } return; }
                    catch { }
                }
                last = len;
                Thread.Sleep(400);
            }
        }

        static string Tail(string path)
        {
            try
            {
                if (!File.Exists(path)) return "(the VM wrote no log - the agent may not have started, or antivirus removed it)";
                string[] all = File.ReadAllLines(path);
                int take = Math.Min(all.Length, 15);
                return string.Join("\r\n", all.Skip(all.Length - take));
            }
            catch (Exception ex) { return "(could not read the VM log: " + ex.Message + ")"; }
        }

        public class JobResult { public long Freed; public int Deleted, Skipped; }

        // Reads the VM's Recycle Bin contents.
        public static List<BinItem> RecycleList(string host, Action<string> status, Func<bool> cancel)
        {
            string text = RunTextJob(host, "/recyclelist", null, status, cancel);
            var list = new List<BinItem>();
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] p = line.Split('\t');
                if (p.Length < 6) continue;
                long sz; long ft; long.TryParse(p[0], out sz); long.TryParse(p[1], out ft);
                list.Add(new BinItem { Size = sz, Deleted = ft > 0 ? SafeTime(ft) : DateTime.MinValue, OriginalPath = p[2], InfoPath = p[3], DataPath = p[4], IsFolder = p[5] == "1", Selected = false });
            }
            return list;
        }

        // Permanently deletes the given Recycle Bin items on the VM (or empties all).
        public static JobResult RecycleDelete(string host, IEnumerable<string> infoDataLines, Action<string> status, Func<bool> cancel)
        {
            string text = RunTextJob(host, "/recycledel", infoDataLines, status, cancel);
            var r = new JobResult();
            string[] p = text.Trim().Split('\t');
            if (p.Length >= 2) { long.TryParse(p[0], out r.Freed); int.TryParse(p[1], out r.Deleted); }
            return r;
        }

        public static string RunPlantUtility(string host, string arguments, Action<string> status, Func<bool> cancel)
        {
            string helper = Path.Combine(Application.StartupPath, "Delete Plant Utility CLI.exe");
            if (!File.Exists(helper))
                throw new FileNotFoundException("The Delete Plant Utility CLI helper must be placed beside SpaceAnalyzer.exe.", helper);
            string uncTemp = "\\\\" + host + "\\C$\\Windows\\Temp";
            string remoteExe = uncTemp + "\\Delete Plant Utility CLI.exe";
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string outputLocal = "C:\\Windows\\Temp\\SA_sem_" + stamp + ".json";
            string outputUnc = uncTemp + "\\SA_sem_" + stamp + ".json";
            if (status != null) status("Starting SEM site lookup on " + host + "...");
            try { File.Copy(helper, remoteExe, true); }
            catch (Exception ex) { throw new Exception("Could not copy the SEM helper to " + host + ": " + ex.Message); }
            try { if (File.Exists(outputUnc)) File.Delete(outputUnc); } catch { }
            string cmd = "\"C:\\Windows\\Temp\\Delete Plant Utility CLI.exe\" " + arguments +
                         " --output \"" + outputLocal + "\"";
            uint pid = WmiCreate(host, cmd);
            DateTime stop = DateTime.Now.AddMinutes(30);
            DateTime? ended = null;
            while (DateTime.Now < stop)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                if (File.Exists(outputUnc)) break;
                if (WmiAlive(host, pid)) ended = null;
                else if (ended == null) ended = DateTime.Now;
                else if ((DateTime.Now - ended.Value).TotalSeconds > 45)
                    throw new Exception("The SEM helper stopped on " + host + " without returning a result file.");
                if (status != null) status("Reading SEM site data on " + host + "...");
                Thread.Sleep(900);
            }
            if (!File.Exists(outputUnc)) throw new Exception("Timed out waiting for the SEM helper on " + host + ".");
            WaitUntilStable(outputUnc, cancel);
            string result = File.ReadAllText(outputUnc);
            try { File.Delete(outputUnc); } catch { }
            return result;
        }

        // Generic: run an agent mode with an optional job file, return the .done text.
        static string RunTextJob(string host, string mode, IEnumerable<string> lines, Action<string> status, Func<bool> cancel)
        {
            string uncTemp = "\\\\" + host + "\\C$\\Windows\\Temp";
            string agentUnc = uncTemp + "\\SpaceAnalyzerAgent.exe";
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string jobLocal = "C:\\Windows\\Temp\\SA_rb_" + stamp + ".txt";
            string jobUnc = uncTemp + "\\SA_rb_" + stamp + ".txt";
            string doneUnc = jobUnc + ".done";
            string errUnc = jobUnc + ".err";

            if (status != null) status("Talking to " + host + "...");
            try { if (!File.Exists(agentUnc)) File.Copy(Application.ExecutablePath, agentUnc, true); } catch { }
            File.WriteAllText(jobUnc, lines != null ? string.Join("\r\n", lines) : "");
            try { if (File.Exists(doneUnc)) File.Delete(doneUnc); } catch { }

            string cmd = "\"C:\\Windows\\Temp\\SpaceAnalyzerAgent.exe\" " + mode + " \"" + jobLocal + "\"";
            uint pid = WmiCreate(host, cmd);
            DateTime stop = DateTime.Now.AddMinutes(5);
            while (DateTime.Now < stop)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                Thread.Sleep(800);
                if (File.Exists(doneUnc)) break;
                if (File.Exists(errUnc)) throw new Exception("The VM reported: " + SafeRead(errUnc));
                if (!WmiAlive(host, pid)) { Thread.Sleep(800); if (File.Exists(doneUnc)) break; if (File.Exists(errUnc)) throw new Exception("The VM reported: " + SafeRead(errUnc)); throw new Exception("The VM did not respond."); }
            }
            if (!File.Exists(doneUnc)) throw new Exception("Timed out talking to " + host + ".");
            WaitUntilStable(doneUnc, cancel);
            string text = File.ReadAllText(doneUnc);
            try { File.Delete(doneUnc); File.Delete(jobUnc); } catch { }
            return text;
        }

        // Asks the VM for the files directly inside one folder. Returns them as FileHit list.
        public static List<FileHit> ListFolder(string host, string folder, Action<string> status, Func<bool> cancel)
        {
            string uncTemp = "\\\\" + host + "\\C$\\Windows\\Temp";
            string agentUnc = uncTemp + "\\SpaceAnalyzerAgent.exe";
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string jobLocal = "C:\\Windows\\Temp\\SA_ls_" + stamp + ".txt";
            string jobUnc = uncTemp + "\\SA_ls_" + stamp + ".txt";
            string doneUnc = jobUnc + ".done";
            string errUnc = jobUnc + ".err";

            status("Asking " + host + " for the files in that folder...");
            try { if (!File.Exists(agentUnc)) File.Copy(Application.ExecutablePath, agentUnc, true); } catch { }
            File.WriteAllText(jobUnc, folder);
            try { if (File.Exists(doneUnc)) File.Delete(doneUnc); } catch { }

            string cmd = "\"C:\\Windows\\Temp\\SpaceAnalyzerAgent.exe\" /listdir \"" + jobLocal + "\"";
            uint pid = WmiCreate(host, cmd);

            DateTime stop = DateTime.Now.AddMinutes(5);
            while (DateTime.Now < stop)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                Thread.Sleep(800);
                if (File.Exists(doneUnc)) break;
                if (File.Exists(errUnc)) throw new Exception("The VM reported: " + SafeRead(errUnc));
                if (!WmiAlive(host, pid)) { Thread.Sleep(800); if (File.Exists(doneUnc)) break; if (File.Exists(errUnc)) throw new Exception("The VM reported: " + SafeRead(errUnc)); throw new Exception("The VM did not return the folder's files."); }
            }
            if (!File.Exists(doneUnc)) throw new Exception("Timed out listing the folder on " + host + ".");
            WaitUntilStable(doneUnc, cancel);
            string text = File.ReadAllText(doneUnc);
            try { File.Delete(doneUnc); File.Delete(jobUnc); } catch { }

            var list = new List<FileHit>();
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] p = line.Split('\t');
                if (p.Length < 3) continue;
                long sz; long ft;
                long.TryParse(p[0], out sz); long.TryParse(p[1], out ft);
                list.Add(new FileHit { Size = sz, Modified = ft > 0 ? SafeTime(ft) : DateTime.MinValue, Path = p[2], Selected = false });
            }
            return list;
        }

        // Sends a list of paths to delete (or rule titles to clean) and waits for the result.
        public static JobResult RunJob(string host, string mode, IEnumerable<string> lines, Action<string> status, Func<bool> cancel)
        {
            string uncTemp = "\\\\" + host + "\\C$\\Windows\\Temp";
            string agentUnc = uncTemp + "\\SpaceAnalyzerAgent.exe";
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string jobLocal = "C:\\Windows\\Temp\\SA_job_" + stamp + ".txt";
            string jobUnc = uncTemp + "\\SA_job_" + stamp + ".txt";
            string doneUnc = jobUnc + ".done";
            string errUnc = jobUnc + ".err";

            status("Preparing the job on " + host + "...");
            try { if (!File.Exists(agentUnc)) File.Copy(Application.ExecutablePath, agentUnc, true); } catch { }
            File.WriteAllText(jobUnc, string.Join("\r\n", lines));
            try { if (File.Exists(doneUnc)) File.Delete(doneUnc); } catch { }
            try { if (File.Exists(errUnc)) File.Delete(errUnc); } catch { }

            string cmd = "\"C:\\Windows\\Temp\\SpaceAnalyzerAgent.exe\" " + mode + " \"" + jobLocal + "\"";
            status(mode == "/cleanup" ? "Cleaning on " + host + "..." : "Deleting on " + host + "...");
            uint pid = WmiCreate(host, cmd);

            DateTime stop = DateTime.Now.AddMinutes(TimeoutMinutes);
            while (DateTime.Now < stop)
            {
                if (cancel()) throw new OperationCanceledException();
                Thread.Sleep(1200);
                if (File.Exists(doneUnc)) break;
                if (File.Exists(errUnc)) throw new Exception("The VM reported: " + SafeRead(errUnc));
                if (!WmiAlive(host, pid)) { Thread.Sleep(1000); if (File.Exists(doneUnc)) break; if (File.Exists(errUnc)) throw new Exception("The VM reported: " + SafeRead(errUnc)); throw new Exception("The job ended on " + host + " without reporting a result."); }
            }
            if (!File.Exists(doneUnc)) throw new Exception("Timed out waiting for " + host + ".");
            string[] parts = SafeRead(doneUnc).Split('\t');
            var r = new JobResult();
            if (parts.Length >= 3) { long.TryParse(parts[0], out r.Freed); int.TryParse(parts[1], out r.Deleted); int.TryParse(parts[2], out r.Skipped); }
            try { File.Delete(doneUnc); File.Delete(jobUnc); } catch { }
            return r;
        }

        // ---- WMI (built into Windows) ----
        public static uint WmiCreate(string host, string commandLine)
        {
            var opts = new System.Management.ConnectionOptions
            {
                Impersonation = System.Management.ImpersonationLevel.Impersonate,
                Authentication = System.Management.AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true
            };
            var scope = new System.Management.ManagementScope("\\\\" + host + "\\root\\cimv2", opts);
            try { scope.Connect(); }
            catch (Exception ex) { Log.Line("  WMI connect failed: " + ex.Message); throw new Exception("Could not connect to " + host + " to start the scan (WMI). " + Hint(ex.Message)); }
            using (var mc = new System.Management.ManagementClass(scope, new System.Management.ManagementPath("Win32_Process"), null))
            using (var inp = mc.GetMethodParameters("Create"))
            {
                inp["CommandLine"] = commandLine;
                using (var outp = mc.InvokeMethod("Create", inp, null))
                {
                    uint rc = Convert.ToUInt32(outp["ReturnValue"]);
                    if (rc != 0) throw new Exception("The VM refused to start the scan (code " + rc + "): " + WmiMsg(rc));
                    return Convert.ToUInt32(outp["ProcessId"]);
                }
            }
        }

        public static bool WmiAlive(string host, uint pid)
        {
            try
            {
                var opts = new System.Management.ConnectionOptions
                {
                    Impersonation = System.Management.ImpersonationLevel.Impersonate,
                    Authentication = System.Management.AuthenticationLevel.PacketPrivacy
                };
                var scope = new System.Management.ManagementScope("\\\\" + host + "\\root\\cimv2", opts);
                scope.Connect();
                var q = new System.Management.ObjectQuery("SELECT ProcessId FROM Win32_Process WHERE ProcessId=" + pid);
                using (var searcher = new System.Management.ManagementObjectSearcher(scope, q))
                using (var res = searcher.Get())
                    return res.Count > 0;
            }
            catch { return true; }   // if we can't tell, assume still running and keep waiting for the file
        }

        static string WmiMsg(uint rc)
        {
            switch (rc)
            {
                case 2: return "access denied";
                case 3: return "insufficient privilege";
                case 8: return "unknown failure";
                case 9: return "path not found";
                case 21: return "invalid parameter";
                default: return "see Microsoft's Win32_Process codes";
            }
        }

        static string Hint(string m)
        {
            if (m.IndexOf("0x800706BA", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("RPC server", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The VM may be off, or its firewall is blocking WMI (allow 'Windows Management Instrumentation (WMI-In)'). (" + m + ")";
            if (m.IndexOf("0x80070005", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Access", StringComparison.OrdinalIgnoreCase) >= 0 && m.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Your account isn't allowed to run WMI on that VM. Run Space Analyzer as a domain administrator. (" + m + ")";
            return m;
        }

        // ---- parse the structured result ----
        static RemoteResult Parse(string host, string text)
        {
            var res = new RemoteResult { Host = host };
            var stack = new List<DirNode>();
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] p = line.Split('\t');
                switch (p[0])
                {
                    case "DV":
                        res.Drives.Add(new DriveMeta { Letter = p.Length > 1 ? p[1] : "", Total = L(p, 2), Free = L(p, 3) });
                        break;
                    case "INI":
                        if (p.Length > 1 && !string.IsNullOrWhiteSpace(p[1])) res.IniPaths.Add(p[1]);
                        break;
                    case "SCAN":
                        res.Files = L(p, 1); res.Bytes = L(p, 2);
                        double.TryParse(p.Length > 3 ? p[3] : "0", System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out res.Seconds);
                        res.FastDrives = (int)L(p, 4); res.SlowDrives = (int)L(p, 5);
                        break;
                    case "F":
                    {
                        int depth = (int)L(p, 1);
                        var n = new DirNode
                        {
                            Size = L(p, 2), FileCount = L(p, 3), OwnSize = L(p, 4), OwnCount = L(p, 5),
                            FullPath = p.Length > 6 ? p[6] : ""
                        };
                        if (string.IsNullOrEmpty(n.FullPath)) { n.IsVirtual = true; n.Name = "All selected drives"; }
                        else n.Name = NameOf(n.FullPath);
                        if (depth == 0) { res.Root = n; stack.Clear(); stack.Add(n); }
                        else
                        {
                            if (stack.Count > depth) stack.RemoveRange(depth, stack.Count - depth);
                            if (stack.Count >= 1 && depth - 1 < stack.Count)
                            {
                                DirNode parent = stack[depth - 1];
                                n.Parent = parent; parent.Children.Add(n);
                            }
                            stack.Add(n);
                        }
                        break;
                    }
                    case "XF":
                    {
                        string path = p.Length > 4 ? p[4] : "";
                        long ft = L(p, 2);
                        res.TopFiles.Add(new FileEntry
                        {
                            Size = L(p, 1),
                            Modified = ft > 0 ? SafeTime(ft) : DateTime.MinValue,
                            Ext = p.Length > 3 ? p[3] : "",
                            FullPath = path,
                            Name = NameOf(path),
                            Folder = new DirNode { FullPath = DirOf(path) }
                        });
                        break;
                    }
                    case "XE":
                        res.Exts[p.Length > 3 ? p[3] : "?"] = new ExtStat { Ext = p.Length > 3 ? p[3] : "?", Size = L(p, 1), Count = L(p, 2) };
                        break;
                    case "AD":
                        res.Adv.Add(new AdvRow { Id = (int)L(p, 1), Level = (int)L(p, 2), Kind = (int)L(p, 3), Size = L(p, 4), Count = L(p, 5), Title = p.Length > 6 ? p[6] : "" });
                        break;
                    case "AF":
                    {
                        int adId = (int)L(p, 1);
                        AdvRow row = res.Adv.FirstOrDefault(a => a.Id == adId);
                        if (row != null)
                        {
                            long ft = L(p, 3);
                            row.Files.Add(new FileHit { Size = L(p, 2), Modified = ft > 0 ? SafeTime(ft) : DateTime.MinValue, Path = p.Length > 4 ? p[4] : "", Selected = true });
                        }
                        break;
                    }
                }
            }
            if (res.Root != null) AddRemainders(res.Root);
            return res;
        }

        static void AddRemainders(DirNode n)
        {
            long childSize = 0, childCount = 0;
            foreach (DirNode c in n.Children) { childSize += c.Size; childCount += c.FileCount; AddRemainders(c); }
            long rem = n.Size - n.OwnSize - childSize;
            long remCount = n.FileCount - n.OwnCount - childCount;
            if (rem > 64 * 1024)   // lump the small sub-folders we didn't list into one entry
                n.Children.Add(new DirNode { Name = "(smaller sub-folders)", FullPath = "", IsVirtual = true, Size = rem, FileCount = remCount > 0 ? remCount : 0 });
            n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }

        static long L(string[] p, int i) { long v; return i < p.Length && long.TryParse(p[i], out v) ? v : 0; }
        static DateTime SafeTime(long ft) { try { return DateTime.FromFileTime(ft); } catch { return DateTime.MinValue; } }
        static string NameOf(string path) { if (string.IsNullOrEmpty(path)) return ""; string t = path.TrimEnd('\\'); int i = t.LastIndexOf('\\'); return i >= 0 ? t.Substring(i + 1) : t; }
        static string DirOf(string path) { try { return Path.GetDirectoryName(path) ?? path; } catch { return path; } }
    }

    // ==================================================================
    //  NETWORK VMs: find domain computers and scan the one you pick
    //  over Windows admin shares (\\PC\C$). No Azure sign-in needed.
    // ==================================================================
    class NetMachine
    {
        public string Name, Dns, Os, Status = "Not checked";
        public List<string> Drives = new List<string>();  // admin-share roots like \\PC\C$
        public bool Online, Accessible;
    }

    static class NetDiscovery
    {
        // Lists computer accounts from Active Directory (the company domain).
        // target can be empty (use this computer's domain), or a domain name
        // like "octave.com", or a domain controller name.
        public static List<NetMachine> FromDomain(string target, out string error)
        {
            error = null;
            var list = new List<NetMachine>();
            try
            {
                string prefix = string.IsNullOrEmpty(target) ? "" : target.Trim().TrimEnd('/') + "/";
                string nc;
                using (var rootDse = new System.DirectoryServices.DirectoryEntry("LDAP://" + prefix + "RootDSE"))
                {
                    object val = rootDse.Properties["defaultNamingContext"].Value;
                    if (val == null) throw new Exception("No directory answered.");
                    nc = val.ToString();
                }
                using (var root = new System.DirectoryServices.DirectoryEntry("LDAP://" + prefix + nc))
                using (var searcher = new System.DirectoryServices.DirectorySearcher(root))
                {
                    // enabled computer accounts only
                    searcher.Filter = "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";
                    searcher.PageSize = 1000;
                    searcher.SizeLimit = 0;
                    searcher.PropertiesToLoad.Add("name");
                    searcher.PropertiesToLoad.Add("dNSHostName");
                    searcher.PropertiesToLoad.Add("operatingSystem");
                    using (System.DirectoryServices.SearchResultCollection results = searcher.FindAll())
                        foreach (System.DirectoryServices.SearchResult r in results)
                        {
                            var m = new NetMachine
                            {
                                Name = Prop(r, "name"),
                                Dns = Prop(r, "dNSHostName"),
                                Os = Prop(r, "operatingSystem")
                            };
                            if (string.IsNullOrEmpty(m.Name)) continue;
                            if (string.IsNullOrEmpty(m.Dns)) m.Dns = m.Name;
                            list.Add(m);
                        }
                }
            }
            catch (Exception ex)
            {
                string m = ex.Message;
                if (m.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.IndexOf("could not be contacted", StringComparison.OrdinalIgnoreCase) >= 0)
                    error = "No domain could be contacted from this computer. If this PC isn't joined to the domain, type your domain name (for example octave.com) when asked, or just add the VM names yourself.";
                else
                    error = m;
            }
            return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string Prop(System.DirectoryServices.SearchResult r, string name)
        {
            return r.Properties.Contains(name) && r.Properties[name].Count > 0 ? r.Properties[name][0].ToString() : "";
        }

        public static string ListPath { get { return Path.Combine(Application.StartupPath, "NetworkVMs.csv"); } }

        public static void EnsureListFile()
        {
            if (File.Exists(ListPath)) return;
            File.WriteAllText(ListPath,
                "# Saved VM list for Space Analyzer\r\n" +
                "# One computer per line. Columns:  Name , Address (optional) , Notes (optional)\r\n" +
                "# Lines starting with # are ignored. Example:\r\n" +
                "# VM-APP01 , vm-app01.octave.com ,\r\n", Encoding.UTF8);
        }

        public static List<NetMachine> LoadList()
        {
            var list = new List<NetMachine>();
            try
            {
                if (!File.Exists(ListPath)) return list;
                foreach (string raw in File.ReadAllLines(ListPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split(',');
                    string name = p[0].Trim();
                    if (name.Length == 0) continue;
                    list.Add(new NetMachine
                    {
                        Name = name,
                        Dns = p.Length > 1 && p[1].Trim().Length > 0 ? p[1].Trim() : name,
                        Os = p.Length > 2 ? p[2].Trim() : ""
                    });
                }
            }
            catch { }
            return list;
        }

        public static void SaveList(IEnumerable<NetMachine> machines)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Saved VM list for Space Analyzer. Columns: Name , Address , Notes");
                foreach (NetMachine m in machines)
                    sb.AppendLine(string.Join(" , ", m.Name, m.Dns ?? m.Name, m.Os ?? ""));
                File.WriteAllText(ListPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // Quick "is it on" test: can we open port 445 (file sharing) within ~1 second?
        public static bool IsOnline(string host)
        {
            // Try the file-sharing port, then RPC, then a ping. Each gets a realistic
            // timeout so a reachable VM is not wrongly called offline on first contact.
            if (TryPort(host, 445, 2500)) return true;
            if (TryPort(host, 135, 2000)) return true;   // RPC endpoint mapper
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var reply = ping.Send(host, 2000);
                    if (reply != null && reply.Status == System.Net.NetworkInformation.IPStatus.Success) return true;
                }
            }
            catch { }
            return false;
        }

        static bool TryPort(string host, int port, int timeoutMs)
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (!ok) return false;
                    try { c.EndConnect(ar); } catch { return false; }
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        // Which drives can we actually reach on that machine (C$ .. Z$)?
        public static List<string> ProbeDrives(string host, out bool accessDenied)
        {
            var found = new List<string>();
            object gate = new object();
            bool denied = false;
            var letters = new List<char>();
            for (char d = 'C'; d <= 'Z'; d++) letters.Add(d);
            // Check each possible drive. The system drive (C:) on a busy VM can be slow to answer,
            // so we try twice and wait a bit longer before giving up on a letter.
            System.Threading.Tasks.Parallel.ForEach(letters,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 12 }, d =>
            {
                string unc = "\\\\" + host + "\\" + d + "$";
                bool exists = false, den = false;
                for (int attempt = 0; attempt < 2 && !exists; attempt++)
                {
                    bool a_exists = false, a_den = false;
                    var t = new Thread(() =>
                    {
                        try { a_exists = Directory.Exists(unc); }
                        catch (UnauthorizedAccessException) { a_den = true; }
                        catch { }
                    });
                    t.IsBackground = true; t.Start();
                    if (t.Join(2500)) { exists = a_exists; den = a_den; }
                }
                lock (gate) { if (den) denied = true; if (exists) found.Add(unc); }
            });
            accessDenied = denied && found.Count == 0;
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }
    }

    // ------------------------------------------------------------------
    //  Window: Network VMs
    // ------------------------------------------------------------------
    class NetworkForm : Form
    {
        // Runs an action on the UI thread, but never crashes if the window isn't ready yet.
        void SafeInvoke(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch { }
        }

        readonly MainForm main;
        List<NetMachine> machines = new List<NetMachine>();
        DataGridView grid;
        SearchBox filterBox;
        Label lblStatus;
        FlatBtn btnFind, btnAdd, btnImport, btnReload, btnRemove, btnEdit, btnCheck, btnScan;
        System.Windows.Forms.Timer timer;
        volatile bool busy;
        volatile string work = "";
        int checkedCount, checkTotal;

        public NetworkForm(MainForm owner)
        {
            main = owner;
            Text = "Network VMs";
            BackColor = Theme.Bg; ForeColor = Theme.Ink; Font = Theme.Body;
            Size = new Size(1120, 700);
            MinimumSize = new Size(900, 540);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Bg };
            var title = new Label { Text = "Network VMs", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(18, 14) };
            var sub = new Label
            {
                Text = "Find the computers in your domain, check which ones you can reach, and scan the one you pick over the network. " +
                       "You need administrator rights on that computer.",
                Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false, Size = new Size(760, 38), Location = new Point(20, 46)
            };
            header.Controls.Add(title); header.Controls.Add(sub);
            btnAdd = new FlatBtn("Add VM by name", BtnKind.Primary); btnAdd.Width = 150;
            btnImport = new FlatBtn("Import list", BtnKind.Secondary);
            btnReload = new FlatBtn("Reload saved list", BtnKind.Secondary);
            btnEdit = new FlatBtn("Edit saved list", BtnKind.Secondary);
            btnRemove = new FlatBtn("Remove selected", BtnKind.Secondary);
            btnAdd.Click += (s, e) => AddByName();
            btnImport.Click += (s, e) => ImportList();
            btnReload.Click += (s, e) => { machines = NetDiscovery.LoadList(); Fill(); lblStatus.Text = machines.Count + " VM(s) in your saved list."; };
            btnEdit.Click += (s, e) => EditListFile();
            btnRemove.Click += (s, e) => RemoveSelected();
            var top = RightFlow(header, 22, btnAdd, btnImport);
            header.Controls.Add(top);
            header.Resize += (s, e) => { sub.Width = Math.Max(300, header.Width - top.Width - 40); top.Left = header.Width - top.Width; };

            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(20, 6, 20, 6), BackColor = Theme.Bg };
            filterBox = new SearchBox("Filter by name"); filterBox.Dock = DockStyle.Left; filterBox.Width = 300;
            filterBox.Box.TextChanged += (s, e) => Fill();
            var listTools = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, AutoSize = true };
            listTools.Controls.Add(btnRemove); listTools.Controls.Add(btnEdit); listTools.Controls.Add(btnReload);
            filterPanel.Controls.Add(listTools);
            filterPanel.Controls.Add(filterBox);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0), BackColor = Theme.Bg };
            var card = new Card { Title = "Computers", Subtitle = "Double-click a reachable computer to scan it" };
            grid = GridStyle.Make();
            grid.MultiSelect = true;
            grid.Columns.Add(GridStyle.Col("name", "Name", 0, 28, false));
            grid.Columns.Add(GridStyle.Col("status", "Status", 150, 0, false));
            grid.Columns.Add(GridStyle.Col("drives", "Drives reachable", 160, 0, false));
            grid.Columns.Add(GridStyle.Col("os", "Operating system", 0, 30, false));
            grid.Columns.Add(GridStyle.Col("dns", "Address", 0, 30, false));
            grid.CellPainting += PaintStatus;
            grid.SelectionChanged += (s, e) => UpdateButtons();
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) ScanSelected(); };
            card.Controls.Add(grid);
            body.Controls.Add(card);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg };
            lblStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 0, 0, 0), ForeColor = Theme.Muted, Font = Theme.Small, AutoEllipsis = true, Text = "Click \"Find in domain\" to list your company's computers, or add names yourself." };
            btnCheck = new FlatBtn("Check reachability", BtnKind.Secondary);
            btnScan = new FlatBtn("Scan selected VM(s)", BtnKind.Primary); btnScan.Width = 180;
            btnCheck.Click += (s, e) => CheckAll();
            btnScan.Click += (s, e) => ScanSelected();
            var fbtns = RightFlow(footer, 14, btnScan, btnCheck);
            footer.Controls.Add(lblStatus); footer.Controls.Add(fbtns);
            footer.Resize += (s, e) => fbtns.Left = footer.Width - fbtns.Width;

            Controls.Add(body); Controls.Add(filterPanel); Controls.Add(footer); Controls.Add(header);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && !busy) Close(); };
            timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (s, e) =>
            {
                if (work.Length > 0) lblStatus.Text = checkTotal > 0 ? string.Format("{0} ({1} of {2})", work, checkedCount, checkTotal) : work;
            };
            NetDiscovery.EnsureListFile();
            machines = NetDiscovery.LoadList();
            Fill();
            UpdateButtons();
            if (machines.Count > 0) { lblStatus.Text = "Checking which VMs are online..."; CheckAll(); }
            else lblStatus.Text = "No saved VMs yet. Click \"Add VM by name\" or \"Import list\".";
        }

        FlowLayoutPanel RightFlow(Control parent, int top, params Control[] ctrls)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, Height = 46, Top = top };
            int w = 8;
            foreach (Control c in ctrls) { f.Controls.Add(c); w += c.Width + c.Margin.Horizontal; }
            f.Width = w; f.Left = parent.Width - w; f.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            return f;
        }

        void PaintStatus(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "status") return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);
            string val = Convert.ToString(e.Value) ?? "";
            Color dot = val.IndexOf("Ready", StringComparison.OrdinalIgnoreCase) >= 0 ? Color.FromArgb(30, 122, 80)
                      : val.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.Danger
                      : val.IndexOf("Offline", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.Faint
                      : val.Equals("Online", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(30, 122, 80)
                      : val.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.Warn : Theme.Gray;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; var r = e.CellBounds;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, r.X + 10, r.Y + r.Height / 2f - 5, 10, 10);
            TextRenderer.DrawText(g, val, Theme.Body, new Rectangle(r.X + 28, r.Y, r.Width - 32, r.Height), Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            e.Handled = true;
        }

        void FindInDomain() { FindInDomain(""); }

        void FindInDomain(string target)
        {
            if (busy) return;
            busy = true; work = "Searching Active Directory..."; UpdateButtons(); timer.Start();
            new Thread(() =>
            {
                string err; List<NetMachine> list = NetDiscovery.FromDomain(target, out err);
                SafeInvoke((Action)(() =>
                {
                    busy = false; timer.Stop(); work = "";
                    if (err != null)
                    {
                        lblStatus.Text = "Could not search the domain.";
                        string entered = Prompt("Search a domain",
                            err + "\r\n\r\nEnter your domain name (for example octave.com) or a domain controller name, then click Add. Leave blank to cancel:");
                        if (!string.IsNullOrEmpty(entered) && entered.Trim().Length > 0) FindInDomain(entered.Trim());
                        else UpdateButtons();
                        return;
                    }
                    Merge(list);
                    SaveNow();
                    lblStatus.Text = string.Format("Found {0} computer(s). Click \"Check reachability\" to see which you can scan.", list.Count);
                    UpdateButtons();
                }));
            }) { IsBackground = true }.Start();
        }

        void Merge(List<NetMachine> list)
        {
            var have = new HashSet<string>(machines.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            foreach (NetMachine m in list) if (have.Add(m.Name)) machines.Add(m);
            machines = machines.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Fill();
        }

        void SaveNow() { NetDiscovery.SaveList(machines); }

        void RemoveSelected()
        {
            NetMachine m = Selected;
            if (m == null) return;
            if (MessageBox.Show(this, "Remove " + m.Name + " from your saved list?", "Remove VM",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            machines.Remove(m);
            SaveNow();
            Fill();
            lblStatus.Text = "Removed " + m.Name + ". " + machines.Count + " VM(s) in your list.";
        }

        void EditListFile()
        {
            NetDiscovery.EnsureListFile();
            SaveNow();
            try { Process.Start(NetDiscovery.ListPath); }
            catch { Util.OpenInExplorer(NetDiscovery.ListPath, true); }
            lblStatus.Text = "Edit the file, save it, then click \"Reload saved list\".";
        }

        void AddByName()
        {
            string text = Prompt("Add computers", "Type one or more computer names or addresses, separated by commas or new lines:");
            if (string.IsNullOrEmpty(text)) return;
            var list = new List<NetMachine>();
            foreach (string raw in text.Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string n = raw.Trim();
                if (n.Length > 0) list.Add(new NetMachine { Name = n, Dns = n });
            }
            Merge(list);
            SaveNow();
            lblStatus.Text = "Added " + list.Count + " machine(s). Your list is saved.";
        }

        void ImportList()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Text or CSV (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*";
                dlg.Title = "Pick a text file with one computer name per line";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var list = new List<NetMachine>();
                    foreach (string line in File.ReadAllLines(dlg.FileName))
                    {
                        string n = line.Split(',')[0].Trim();
                        if (n.Length > 0 && !n.StartsWith("#")) list.Add(new NetMachine { Name = n, Dns = n });
                    }
                    Merge(list);
                    SaveNow();
                    lblStatus.Text = "Imported " + list.Count + " machine(s). Your list is saved.";
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not read the file: " + ex.Message, "Import list", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        IEnumerable<NetMachine> VisibleMachines()
        {
            string f = filterBox.Box.Text.Trim();
            return f.Length == 0 ? machines : machines.Where(m => m.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void Fill()
        {
            grid.Rows.Clear();
            foreach (NetMachine m in VisibleMachines())
            {
                int i = grid.Rows.Add(m.Name, m.Status, string.Join("  ", m.Drives.Select(DriveLabel)), m.Os, m.Dns);
                grid.Rows[i].Tag = m;
            }
            grid.ClearSelection();
            UpdateButtons();
        }

        static string DriveLabel(string unc)
        {
            int i = unc.LastIndexOf('\\');
            return i >= 0 ? unc.Substring(i + 1).Replace("$", ":") : unc;
        }

        void CheckAll()
        {
            if (busy) return;
            List<NetMachine> list = VisibleMachines().ToList();
            if (list.Count == 0) return;
            busy = true; checkedCount = 0; checkTotal = list.Count; work = "Checking which computers are reachable";
            UpdateButtons(); timer.Start();
            new Thread(() =>
            {
                var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 };
                System.Threading.Tasks.Parallel.ForEach(list, opts, m =>
                {
                    m.Online = NetDiscovery.IsOnline(m.Dns ?? m.Name);
                    m.Status = m.Online ? "Online" : "Offline";
                    if (!m.Online) { m.Drives.Clear(); m.Accessible = false; }
                    Interlocked.Increment(ref checkedCount);
                });
                SafeInvoke((Action)(() =>
                {
                    busy = false; timer.Stop(); work = "";
                    Fill();
                    int online = machines.Count(m => m.Online);
                    lblStatus.Text = string.Format("{0} of {1} online. Select VMs and Scan to use them (drives are checked then).", online, list.Count);
                    UpdateButtons();
                }));
            }) { IsBackground = true }.Start();
        }

        NetMachine Selected { get { return grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as NetMachine : null; } }

        List<NetMachine> SelectedMachines()
        {
            return grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as NetMachine).Where(x => x != null).ToList();
        }

        void ScanSelected()
        {
            if (busy) return;
            List<NetMachine> picked = SelectedMachines();
            if (picked.Count == 0) { MessageBox.Show(this, "Tick one or more VMs first.", "Network VMs", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            busy = true; work = "Checking the selected VM(s)..."; checkedCount = 0; checkTotal = picked.Count; UpdateButtons(); timer.Start();
            new Thread(() =>
            {
                foreach (NetMachine m in picked)
                {
                    if (!m.Accessible)
                    {
                        m.Online = NetDiscovery.IsOnline(m.Dns ?? m.Name);
                        bool denied = false;
                        if (m.Online) { m.Drives = NetDiscovery.ProbeDrives(m.Dns ?? m.Name, out denied); m.Accessible = m.Drives.Count > 0; }
                        m.Status = !m.Online ? "Offline" : m.Accessible ? "Ready to scan" : (denied ? "Online, access denied" : "Online, no shares");
                    }
                    Interlocked.Increment(ref checkedCount);
                }
                SafeInvoke((Action)(() => { busy = false; timer.Stop(); work = ""; Fill(); UpdateButtons(); QueuePicked(picked); }));
            }) { IsBackground = true }.Start();
        }

        void QueuePicked(List<NetMachine> picked)
        {
            var ready = picked.Where(m => m.Accessible && m.Drives.Count > 0).ToList();
            var notReady = picked.Where(m => !m.Accessible || m.Drives.Count == 0).ToList();
            if (ready.Count == 0)
            {
                MessageBox.Show(this, "None of the selected VMs can be scanned.\n\nThey may be offline, or you may not be an administrator on them.", "Network VMs", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var jobs = new List<string[]>();
            foreach (NetMachine m in ready)
            {
                var letters = m.Drives.Select(DriveLabel).ToList();   // all drives; letters like "C:"
                var one = new List<string> { m.Name };
                one.AddRange(letters);
                jobs.Add(one.ToArray());
            }

            string msg = "Scan " + ready.Count + " VM(s)?\n\n" + string.Join("\n", ready.Select(m => "  " + m.Name + "  (" + string.Join(",", m.Drives.Select(DriveLabel)) + ")"));
            if (notReady.Count > 0) msg += "\n\nSkipping (not reachable / not admin): " + string.Join(", ", notReady.Select(m => m.Name));
            msg += "\n\nEach VM is scanned on the VM itself and opens as its own tab. Scans run one after another automatically.";
            if (MessageBox.Show(this, msg, "Scan VMs", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

            main.QueueRemoteScans(jobs);
            Close();
        }


        void UpdateButtons()
        {
            btnAdd.Enabled = !busy; btnImport.Enabled = !busy;
            btnReload.Enabled = !busy; btnEdit.Enabled = !busy;
            btnRemove.Enabled = !busy && Selected != null;
            btnCheck.Enabled = !busy && machines.Count > 0;
            btnScan.Enabled = !busy && Selected != null;
        }

        // simple text prompt
        static string Prompt(string title, string label)
        {
            using (var f = new Form())
            {
                f.Text = title; f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = false; f.MaximizeBox = false; f.ClientSize = new Size(460, 190); f.BackColor = Theme.Bg; f.Font = Theme.Body;
                var lbl = new Label { Text = label, Location = new Point(16, 14), Size = new Size(430, 40), ForeColor = Theme.Ink };
                var box = new TextBox { Multiline = true, Location = new Point(16, 56), Size = new Size(428, 80), ScrollBars = ScrollBars.Vertical, Font = Theme.Body };
                var ok = new FlatBtn("Add", BtnKind.Primary) { Location = new Point(268, 146) };
                var cancel = new FlatBtn("Cancel", BtnKind.Secondary) { Location = new Point(360, 146) };
                ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
                f.Controls.Add(lbl); f.Controls.Add(box); f.Controls.Add(ok); f.Controls.Add(cancel);
                return f.ShowDialog() == DialogResult.OK ? box.Text : null;
            }
        }


    }

    // ==================================================================
    //  RECYCLE BIN BROWSER: see what's in the Recycle Bin on every drive
    //  and choose what to permanently delete or restore.
    // ==================================================================
    class BinItem
    {
        public string OriginalPath;   // where the file used to live
        public string DataPath;       // the $R file/folder that holds the content
        public string InfoPath;       // the $I metadata file
        public long Size;
        public DateTime Deleted;
        public bool IsFolder;
        public bool Selected;
        public string Name { get { return Path.GetFileName(OriginalPath.TrimEnd('\\')); } }
        public string OriginalFolder { get { string d = Path.GetDirectoryName(OriginalPath); return d ?? ""; } }
    }

    static class RecycleBin
    {
        // Reads the $I metadata files in every drive's $Recycle.Bin.
        public static List<BinItem> Read(Func<bool> cancel)
        {
            var items = new List<BinItem>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try { if (!d.IsReady || d.DriveType != DriveType.Fixed) continue; }
                catch { continue; }
                string bin = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
                if (!Directory.Exists(bin)) continue;
                string[] sidDirs;
                try { sidDirs = Directory.GetDirectories(bin); } catch { continue; }
                foreach (string sid in sidDirs)
                {
                    if (cancel()) return items;
                    string[] infos;
                    try { infos = Directory.GetFiles(sid, "$I*"); } catch { continue; }
                    foreach (string info in infos)
                    {
                        BinItem it = Parse(info);
                        if (it != null) items.Add(it);
                    }
                }
            }
            return items.OrderByDescending(i => i.Size).ToList();
        }

        static BinItem Parse(string infoPath)
        {
            try
            {
                byte[] b = File.ReadAllBytes(infoPath);
                if (b.Length < 24) return null;
                long version = BitConverter.ToInt64(b, 0);
                long size = BitConverter.ToInt64(b, 8);
                long delTime = BitConverter.ToInt64(b, 16);
                string original;
                if (version >= 2)
                {
                    if (b.Length < 28) return null;
                    int chars = BitConverter.ToInt32(b, 24);
                    int bytes = (chars - 1) * 2;             // drop the trailing null
                    if (bytes < 0 || 28 + bytes > b.Length) bytes = b.Length - 28;
                    original = Encoding.Unicode.GetString(b, 28, bytes);
                }
                else
                {
                    int max = Math.Min(520, b.Length - 24);  // 260 wide chars
                    original = Encoding.Unicode.GetString(b, 24, max).TrimEnd('\0');
                    int nul = original.IndexOf('\0');
                    if (nul >= 0) original = original.Substring(0, nul);
                }
                string dataPath = BuildDataPath(infoPath);
                var it = new BinItem
                {
                    OriginalPath = original,
                    InfoPath = infoPath,
                    DataPath = dataPath,
                    Size = size,
                    IsFolder = dataPath != null && Directory.Exists(dataPath)
                };
                try { it.Deleted = DateTime.FromFileTime(delTime); } catch { it.Deleted = DateTime.MinValue; }
                return it;
            }
            catch { return null; }
        }

        // $I<id><ext>  ->  $R<id><ext>
        static string BuildDataPath(string infoPath)
        {
            string dir = Path.GetDirectoryName(infoPath);
            string file = Path.GetFileName(infoPath);
            if (file.Length < 2 || file[0] != '$' || (file[1] != 'I' && file[1] != 'i')) return null;
            return Path.Combine(dir, "$R" + file.Substring(2));
        }

        public static bool DeleteOne(BinItem it)
        {
            bool ok = true;
            try
            {
                if (it.DataPath != null)
                {
                    if (Directory.Exists(it.DataPath)) Directory.Delete(it.DataPath, true);
                    else if (File.Exists(it.DataPath)) File.Delete(it.DataPath);
                }
            }
            catch { ok = false; }
            try { if (File.Exists(it.InfoPath)) File.Delete(it.InfoPath); } catch { ok = false; }
            return ok;
        }

        public static bool Restore(BinItem it)
        {
            try
            {
                if (it.DataPath == null || !(File.Exists(it.DataPath) || Directory.Exists(it.DataPath))) return false;
                string dir = Path.GetDirectoryName(it.OriginalPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (Directory.Exists(it.DataPath)) Directory.Move(it.DataPath, it.OriginalPath);
                else File.Move(it.DataPath, it.OriginalPath);
                try { if (File.Exists(it.InfoPath)) File.Delete(it.InfoPath); } catch { }
                return true;
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------
    //  Window: Recycle Bin
    // ------------------------------------------------------------------
    class RecycleForm : Form
    {
        List<BinItem> items = new List<BinItem>();
        List<BinItem> view = new List<BinItem>();
        DataGridView grid;
        SearchBox filterBox;
        Label lblInfo;
        FlatBtn btnRefresh, btnRestore, btnDelete, btnEmpty;
        int sortCol = 1;
        bool sortDesc = true;
        readonly string host;   // null = this computer; otherwise a VM

        public RecycleForm() : this(null) { }

        public RecycleForm(string vmHost)
        {
            host = vmHost;
            Text = host == null ? "Recycle Bin - this computer" : "Recycle Bin - " + host;
            BackColor = Theme.Bg; ForeColor = Theme.Ink; Font = Theme.Body;
            Size = new Size(1080, 680);
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Theme.Bg };
            var title = new Label { Text = "Recycle Bin", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(18, 14) };
            var sub = new Label { Text = "Everything waiting in the Recycle Bin on this computer's drives. Tick what you want, then restore or delete it.",
                Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false, Size = new Size(700, 22), Location = new Point(20, 46) };
            header.Controls.Add(title); header.Controls.Add(sub);

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(20, 6, 20, 6), BackColor = Theme.Bg };
            filterBox = new SearchBox("Filter by name or folder"); filterBox.Dock = DockStyle.Left; filterBox.Width = 300;
            filterBox.Box.TextChanged += (s, e) => Rebuild();
            btnRefresh = new FlatBtn("Refresh", BtnKind.Secondary);
            btnRefresh.Click += (s, e) => LoadBin();
            var tool = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, AutoSize = true };
            tool.Controls.Add(btnRefresh);
            toolbar.Controls.Add(tool); toolbar.Controls.Add(filterBox);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0), BackColor = Theme.Bg };
            var card = new Card { Title = "Deleted items", Subtitle = "Click a column title to sort" };
            grid = GridStyle.Make();
            var chk = new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 44, SortMode = DataGridViewColumnSortMode.NotSortable };
            chk.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(chk);
            grid.Columns.Add(GridStyle.Col("name", "Name", 0, 32, false));
            grid.Columns.Add(GridStyle.Col("size", "Size", 96, 0, true));
            grid.Columns.Add(GridStyle.Col("deleted", "Deleted on", 140, 0, false));
            grid.Columns.Add(GridStyle.Col("from", "Original location", 0, 44, false));
            foreach (DataGridViewColumn col in grid.Columns)
                if (col.Index > 0) col.SortMode = DataGridViewColumnSortMode.Programmatic;
            grid.VirtualMode = true;
            grid.CellValueNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= view.Count) return;
                BinItem it = view[e.RowIndex];
                switch (e.ColumnIndex)
                {
                    case 0: e.Value = it.Selected; break;
                    case 1: e.Value = (it.IsFolder ? "[folder]  " : "") + it.Name; break;
                    case 2: e.Value = Util.Size(it.Size); break;
                    case 3: e.Value = it.Deleted == DateTime.MinValue ? "" : it.Deleted.ToString("yyyy-MM-dd HH:mm"); break;
                    case 4: e.Value = it.OriginalFolder; break;
                }
            };
            grid.CellClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) { view[e.RowIndex].Selected = !view[e.RowIndex].Selected; grid.InvalidateRow(e.RowIndex); UpdateInfo(); } };
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) { foreach (DataGridViewRow r in grid.SelectedRows) if (r.Index < view.Count) view[r.Index].Selected = !view[r.Index].Selected; grid.Invalidate(); UpdateInfo(); e.Handled = true; } };
            grid.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.ColumnIndex == 0) return;
                if (sortCol == e.ColumnIndex) sortDesc = !sortDesc; else { sortCol = e.ColumnIndex; sortDesc = e.ColumnIndex == 2 || e.ColumnIndex == 3; }
                Rebuild();
            };
            card.Controls.Add(grid);
            body.Controls.Add(card);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg };
            lblInfo = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 0, 0, 0), ForeColor = Theme.Muted, Font = Theme.Small, AutoEllipsis = true };
            btnEmpty = new FlatBtn("Empty whole bin", BtnKind.Danger);
            btnDelete = new FlatBtn("Delete ticked", BtnKind.Danger);
            btnRestore = new FlatBtn("Restore ticked", BtnKind.Secondary);
            var selAll = new FlatBtn("Tick all shown", BtnKind.Secondary);
            selAll.Click += (s, e) => { foreach (BinItem it in view) it.Selected = true; grid.Invalidate(); UpdateInfo(); };
            btnEmpty.Click += (s, e) => EmptyAll();
            btnDelete.Click += (s, e) => DeleteTicked();
            btnRestore.Click += (s, e) => RestoreTicked();
            var fbtns = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, AutoSize = true, Padding = new Padding(0, 13, 20, 0) };
            foreach (Control c in new Control[] { btnEmpty, btnDelete, btnRestore, selAll }) fbtns.Controls.Add(c);
            footer.Controls.Add(lblInfo); footer.Controls.Add(fbtns);

            Controls.Add(body); Controls.Add(toolbar); Controls.Add(footer); Controls.Add(header);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += (s, e) => LoadBin();
        }

        void LoadBin()
        {
            Cursor = Cursors.WaitCursor;
            lblInfo.Text = host == null ? "Reading the Recycle Bin..." : "Reading the Recycle Bin on " + host + "...";
            Application.DoEvents();
            try { items = host == null ? RecycleBin.Read(() => false) : RemoteAgent.RecycleList(host, null, () => false); }
            catch (Exception ex) { items = new List<BinItem>(); lblInfo.Text = "Could not read " + host + "'s Recycle Bin: " + ex.Message; }
            Cursor = Cursors.Default;
            if (btnRestore != null) btnRestore.Enabled = host == null;   // restore is local-only
            Rebuild();
        }

        void Rebuild()
        {
            string f = filterBox.Box.Text.Trim();
            IEnumerable<BinItem> q = items;
            if (f.Length > 0) q = q.Where(i => i.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || i.OriginalFolder.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            switch (sortCol)
            {
                case 1: q = sortDesc ? q.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase) : q.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase); break;
                case 3: q = sortDesc ? q.OrderByDescending(i => i.Deleted) : q.OrderBy(i => i.Deleted); break;
                case 4: q = sortDesc ? q.OrderByDescending(i => i.OriginalFolder, StringComparer.OrdinalIgnoreCase) : q.OrderBy(i => i.OriginalFolder, StringComparer.OrdinalIgnoreCase); break;
                default: q = sortDesc ? q.OrderByDescending(i => i.Size) : q.OrderBy(i => i.Size); break;
            }
            view = q.ToList();
            foreach (DataGridViewColumn c in grid.Columns) if (c.Index > 0) c.HeaderCell.SortGlyphDirection = SortOrder.None;
            if (grid.Columns[sortCol].SortMode != DataGridViewColumnSortMode.NotSortable)
                grid.Columns[sortCol].HeaderCell.SortGlyphDirection = sortDesc ? SortOrder.Descending : SortOrder.Ascending;
            grid.RowCount = 0; grid.RowCount = view.Count; grid.Invalidate();
            UpdateInfo();
        }

        List<BinItem> Ticked() { return view.Where(i => i.Selected).ToList(); }

        void UpdateInfo()
        {
            long total = items.Sum(i => i.Size);
            List<BinItem> t = Ticked();
            lblInfo.Text = items.Count == 0 ? "The Recycle Bin is empty."
                : string.Format("{0:N0} item(s), {1} total.{2}", items.Count, Util.Size(total),
                    t.Count > 0 ? string.Format("   Ticked: {0:N0}, {1}.", t.Count, Util.Size(t.Sum(i => i.Size))) : "");
            bool any = t.Count > 0;
            btnDelete.Enabled = any; btnRestore.Enabled = any;
            btnEmpty.Enabled = items.Count > 0;
        }

        void DeleteTicked()
        {
            List<BinItem> t = Ticked();
            if (t.Count == 0) return;
            if (MessageBox.Show(this, string.Format("Permanently delete {0:N0} ticked item(s), {1}?\n\nThis cannot be undone.", t.Count, Util.Size(t.Sum(i => i.Size))),
                    "Delete from Recycle Bin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (host == null)
            {
                int ok = 0;
                foreach (BinItem it in t) { if (RecycleBin.DeleteOne(it)) { ok++; items.Remove(it); DeleteLog.Write(it.OriginalPath, it.Size, "Deleted from Recycle Bin"); } }
                Rebuild();
                lblInfo.Text = string.Format("Permanently deleted {0:N0} item(s).", ok);
            }
            else
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    var r = RemoteAgent.RecycleDelete(host, t.Select(i => i.InfoPath + "\t" + i.DataPath), null, () => false);
                    foreach (BinItem it in t) items.Remove(it);
                    DeleteLog.Write(host + " Recycle Bin: " + t.Count + " items", r.Freed, "Deleted from VM Recycle Bin");
                    lblInfo.Text = string.Format("Deleted {0:N0} item(s) on {1}.", r.Deleted, host);
                }
                catch (Exception ex) { lblInfo.Text = "Delete failed: " + ex.Message; }
                Cursor = Cursors.Default;
                Rebuild();
            }
        }

        void RestoreTicked()
        {
            if (host != null) { MessageBox.Show(this, "Restoring isn't available for a VM's Recycle Bin from here.", "Recycle Bin", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            List<BinItem> t = Ticked();
            if (t.Count == 0) return;
            int ok = 0, fail = 0;
            foreach (BinItem it in t) { if (RecycleBin.Restore(it)) { ok++; items.Remove(it); } else fail++; }
            Rebuild();
            lblInfo.Text = string.Format("Restored {0:N0} item(s) to their original place.{1}", ok, fail > 0 ? " " + fail + " could not be restored." : "");
        }

        void EmptyAll()
        {
            if (items.Count == 0) return;
            if (MessageBox.Show(this, string.Format("Permanently empty the whole Recycle Bin ({0:N0} items, {1})?\n\nThis cannot be undone.", items.Count, Util.Size(items.Sum(i => i.Size))),
                    "Empty Recycle Bin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (host == null) Recycler.EmptyRecycleBin(this);
                else RemoteAgent.RecycleDelete(host, new[] { "*EMPTYALL*" }, null, () => false);
            }
            catch (Exception ex) { lblInfo.Text = "Empty failed: " + ex.Message; }
            Cursor = Cursors.Default;
            LoadBin();
            if (!lblInfo.Text.StartsWith("Empty failed")) lblInfo.Text = "Recycle Bin emptied.";
        }
    }

    // ==================================================================
    //  PER-USER ANALYSIS: who owns the most old data, from Windows user
    //  profiles (Users\name) and team folders named by short name.
    // ==================================================================
    class UserStat
    {
        public string Name;
        public long TotalSize, OldSize, OldFiles, TotalFiles;
        public List<string> Sources = new List<string>();
        public string Where { get { return string.Join(", ", Sources.Take(3)) + (Sources.Count > 3 ? " +" + (Sources.Count - 3) : ""); } }
    }

    static class UserFolders
    {
        public static string Path1 { get { return System.IO.Path.Combine(Application.StartupPath, "TeamFolders.txt"); } }

        public static void EnsureFile()
        {
            if (System.IO.File.Exists(Path1)) return;
            System.IO.File.WriteAllText(Path1,
                "# Parent folders that contain one sub-folder per person (named by short name).\r\n" +
                "# One path per line. The tool treats each immediate sub-folder as a user.\r\n" +
                "# Lines starting with # are ignored. Examples:\r\n" +
                "# D:\\Projects\r\n" +
                "# D:\\Users\r\n" +
                "# \\\\fileserver\\Home\r\n", Encoding.UTF8);
        }

        public static List<string> Load()
        {
            var list = new List<string>();
            try
            {
                if (!System.IO.File.Exists(Path1)) return list;
                foreach (string raw in System.IO.File.ReadAllLines(Path1))
                {
                    string l = raw.Trim();
                    if (l.Length > 0 && l[0] != '#') list.Add(l.TrimEnd('\\'));
                }
            }
            catch { }
            return list;
        }
    }

    static class UserAnalyzer
    {
        // Walks the scanned tree. For Users\* and each configured team-folder root,
        // every immediate sub-folder is treated as a person; sizes are summed.
        public static List<UserStat> Analyze(DirNode root, Scanner scanner, int ageDays)
        {
            var map = new Dictionary<string, UserStat>(StringComparer.OrdinalIgnoreCase);
            var teamRoots = UserFolders.Load();

            // find candidate "parent" folders: any node whose name is "Users", plus configured roots
            var parents = new List<DirNode>();
            Walk(root, n =>
            {
                if (n.Name != null && n.Name.Equals("Users", StringComparison.OrdinalIgnoreCase)) parents.Add(n);
                foreach (string tr in teamRoots)
                    if (!string.IsNullOrEmpty(n.FullPath) && n.FullPath.TrimEnd('\\').Equals(tr, StringComparison.OrdinalIgnoreCase)) parents.Add(n);
            });

            DateTime cutoff = DateTime.Now.AddDays(-ageDays);
            // old-file size per folder path, from the scan's known files (top + big lists)
            var oldByFolder = BuildOldIndex(scanner, cutoff);

            foreach (DirNode parent in parents)
            {
                foreach (DirNode userDir in parent.Children)
                {
                    if (userDir.IsVirtual) continue;
                    string name = NormalizeUser(userDir.Name);
                    if (IsSystemProfile(name)) continue;
                    UserStat u;
                    if (!map.TryGetValue(name, out u)) { u = new UserStat { Name = name }; map[name] = u; }
                    u.TotalSize += userDir.Size;
                    u.TotalFiles += userDir.FileCount;
                    u.Sources.Add(userDir.FullPath);

                    // old data: sum old files that fall under this user's folder
                    long oldSize, oldCount;
                    SumOldUnder(userDir, oldByFolder, out oldSize, out oldCount);
                    u.OldSize += oldSize;
                    u.OldFiles += oldCount;
                }
            }

            return map.Values.OrderByDescending(u => u.OldSize).ThenByDescending(u => u.TotalSize).ToList();
        }

        static Dictionary<string, long[]> BuildOldIndex(Scanner scanner, DateTime cutoff)
        {
            // folderPath -> [oldBytes, oldCount] for files we know the dates of
            var d = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            if (scanner == null) return d;
            var seen = new HashSet<FileEntry>();
            foreach (var list in new[] { scanner.TopFiles, scanner.BigFiles })
                foreach (FileEntry f in list)
                {
                    if (!seen.Add(f)) continue;
                    if (f.Modified == DateTime.MinValue || f.Modified >= cutoff) continue;
                    string dir = f.Folder != null ? f.Folder.FullPath : "";
                    if (string.IsNullOrEmpty(dir)) continue;
                    long[] v; if (!d.TryGetValue(dir, out v)) { v = new long[2]; d[dir] = v; }
                    v[0] += f.Size; v[1]++;
                }
            return d;
        }

        static void SumOldUnder(DirNode node, Dictionary<string, long[]> oldByFolder, out long bytes, out long count)
        {
            bytes = 0; count = 0;
            var stack = new Stack<DirNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                DirNode n = stack.Pop();
                long[] v;
                if (n.FullPath != null && oldByFolder.TryGetValue(n.FullPath.TrimEnd('\\'), out v)) { bytes += v[0]; count += v[1]; }
                foreach (DirNode c in n.Children) stack.Push(c);
            }
        }

        static void Walk(DirNode n, Action<DirNode> visit)
        {
            var stack = new Stack<DirNode>();
            stack.Push(n);
            while (stack.Count > 0)
            {
                DirNode x = stack.Pop();
                visit(x);
                foreach (DirNode c in x.Children) stack.Push(c);
            }
        }

        static string NormalizeUser(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int dot = name.IndexOf('.');   // DOMAIN.user or user.DOMAIN sometimes
            return name;
        }

        static readonly HashSet<string> SystemProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Public", "Default", "Default User", "All Users", "defaultuser0", "systemprofile", "LocalService", "NetworkService", "MSSQL", "(smaller sub-folders)" };

        static bool IsSystemProfile(string name)
        {
            return string.IsNullOrEmpty(name) || SystemProfiles.Contains(name) || name.StartsWith("(");
        }
    }

    // Config for scheduled runs: which VMs to include.
    static class ScheduleConfig
    {
        public static string Path1 { get { return System.IO.Path.Combine(Application.StartupPath, "ScheduledVMs.txt"); } }
        public static void SaveVms(IEnumerable<string> vms) { try { System.IO.File.WriteAllText(Path1, string.Join("\r\n", vms), Encoding.UTF8); } catch { } }
        public static List<string> LoadVms()
        {
            var list = new List<string>();
            try { if (System.IO.File.Exists(Path1)) foreach (string l in System.IO.File.ReadAllLines(Path1)) { string t = l.Trim(); if (t.Length > 0 && t[0] != '#') list.Add(t); } } catch { }
            return list;
        }
    }

    // ------------------------------------------------------------------
    //  Window: Schedule maintenance
    // ------------------------------------------------------------------
    class ScheduleForm : Form
    {
        CheckedListBox vmList;
        DateTimePicker timePick;
        CheckBox[] dayBoxes;
        NumericUpDown ageBox;
        Label lblState;

        public ScheduleForm()
        {
            Text = "Schedule maintenance";
            BackColor = Theme.Bg; ForeColor = Theme.Ink; Font = Theme.Body;
            Size = new Size(720, 640);
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Theme.Bg };
            header.Controls.Add(new Label { Text = "Schedule maintenance", Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(18, 14) });
            header.Controls.Add(new Label { Text = "Pick VMs and a time. Windows will run Space Analyzer quietly on that schedule, scan the VMs, and email each folder's owner their old files.", Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false, Size = new Size(660, 36), Location = new Point(20, 44) });

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(20, 4, 20, 4), AutoScroll = true };

            var lbl1 = new Label { Text = "VMs to scan (from your saved list):", AutoSize = true, Location = new Point(20, 8), Font = Theme.BodyBold };
            vmList = new CheckedListBox { Location = new Point(20, 32), Size = new Size(650, 180), CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
            foreach (NetMachine m in NetDiscovery.LoadList()) vmList.Items.Add(m.Name, ScheduleConfig.LoadVms().Contains(m.Name));
            if (vmList.Items.Count == 0) vmList.Items.Add("(no saved VMs - add them in the Network VMs window first)");

            var lbl2 = new Label { Text = "Days:", AutoSize = true, Location = new Point(20, 224), Font = Theme.BodyBold };
            string[] days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            dayBoxes = new CheckBox[7];
            for (int i = 0; i < 7; i++)
            {
                dayBoxes[i] = new CheckBox { Text = days[i], AutoSize = true, Location = new Point(70 + i * 70, 222), Checked = i < 5 };
                body.Controls.Add(dayBoxes[i]);
            }

            var lbl3 = new Label { Text = "Time:", AutoSize = true, Location = new Point(20, 258), Font = Theme.BodyBold };
            timePick = new DateTimePicker { Format = DateTimePickerFormat.Time, ShowUpDown = true, Location = new Point(70, 254), Width = 90, Value = DateTime.Today.AddHours(19) };

            var lbl4 = new Label { Text = "Old files = older than", AutoSize = true, Location = new Point(200, 258) };
            ageBox = new NumericUpDown { Location = new Point(330, 254), Width = 64, Minimum = 30, Maximum = 3650, Value = 180, Increment = 30 };
            var lbl4b = new Label { Text = "days", AutoSize = true, Location = new Point(400, 258) };

            lblState = new Label { Location = new Point(20, 300), Size = new Size(650, 40), ForeColor = Theme.Muted, Font = Theme.Small };
            RefreshState();

            body.Controls.Add(lbl1); body.Controls.Add(vmList);
            body.Controls.Add(lbl2); body.Controls.Add(lbl3); body.Controls.Add(timePick);
            body.Controls.Add(lbl4); body.Controls.Add(ageBox); body.Controls.Add(lbl4b);
            body.Controls.Add(lblState);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg };
            var btnSave = new FlatBtn("Create / update schedule", BtnKind.Primary); btnSave.Width = 210;
            var btnRemove = new FlatBtn("Remove schedule", BtnKind.Danger);
            var btnClose = new FlatBtn("Close", BtnKind.Secondary);
            btnSave.Click += (s, e) => CreateTask();
            btnRemove.Click += (s, e) => RemoveTask();
            btnClose.Click += (s, e) => Close();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, AutoSize = true, Padding = new Padding(0, 13, 20, 0) };
            foreach (Control c in new Control[] { btnSave, btnRemove, btnClose }) flow.Controls.Add(c);
            footer.Controls.Add(flow);

            Controls.Add(body); Controls.Add(footer); Controls.Add(header);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        const string TaskName = "SpaceAnalyzerMaintenance";

        void RefreshState()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Query /TN " + TaskName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var pr = System.Diagnostics.Process.Start(psi)) { pr.WaitForExit(5000); lblState.Text = pr.ExitCode == 0 ? "A schedule already exists. Saving will update it." : "No schedule yet."; }
            }
            catch { lblState.Text = ""; }
        }

        void CreateTask()
        {
            var vms = new List<string>();
            foreach (object it in vmList.CheckedItems) { string n = it.ToString(); if (!n.StartsWith("(")) vms.Add(n); }
            if (vms.Count == 0) { MessageBox.Show(this, "Tick at least one VM.", "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var days = new List<string>();
            string[] codes = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };
            for (int i = 0; i < 7; i++) if (dayBoxes[i].Checked) days.Add(codes[i]);
            if (days.Count == 0) { MessageBox.Show(this, "Pick at least one day.", "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            ScheduleConfig.SaveVms(vms);
            string exe = Application.ExecutablePath;
            string time = timePick.Value.ToString("HH:mm");
            string tr = "\"" + exe + "\" /scheduled /age " + (int)ageBox.Value;
            string args = "/Create /F /TN " + TaskName + " /TR \"" + tr.Replace("\"", "\\\"") + "\" /SC WEEKLY /D " + string.Join(",", days) + " /ST " + time + " /RL HIGHEST";

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var pr = System.Diagnostics.Process.Start(psi))
                {
                    string outp = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd();
                    pr.WaitForExit(8000);
                    if (pr.ExitCode == 0)
                    {
                        MessageBox.Show(this, "Schedule created.\n\nWindows will run the scan on: " + string.Join(", ", days) + " at " + time + ".\n\nMake sure EmailSettings.ini is filled in so owners get emailed. The run writes SpaceAnalyzer_Schedule.log.", "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        RefreshState();
                    }
                    else MessageBox.Show(this, "Windows could not create the task:\n\n" + outp + "\n\nYou may need to run Space Analyzer as administrator.", "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not create the task: " + ex.Message, "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void RemoveTask()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Delete /F /TN " + TaskName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using (var pr = System.Diagnostics.Process.Start(psi)) { pr.WaitForExit(5000); }
                lblState.Text = "No schedule yet.";
                MessageBox.Show(this, "Schedule removed.", "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not remove: " + ex.Message, "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    // ------------------------------------------------------------------
    //  Main window
    // ------------------------------------------------------------------
    // One scanned machine's complete state. The window shows one session at a time.
    class MachineSession
    {
        public string Name;            // "This computer" or a VM name
        public bool IsRemote;
        public List<string> DriveLetters = new List<string>();   // for a VM: drives it has (e.g. "C:")
        public List<string> UncRoots = new List<string>();       // for fallback file-by-file scan
        public List<DriveMeta> Drives = new List<DriveMeta>();   // size/free per drive (VM)
        public List<string> IniPaths = new List<string>();
        public bool IniScanFinished;
        public bool HasResult;

        // scan results
        public Scanner Scanner;
        public DirNode Root, Current;
        public List<AdvRow> Adv;       // advisor numbers from this machine
        public long Files, Bytes;
        public double Seconds;
    }

    // A simple checkable tile for a VM's drive (shown when viewing a network VM).
    class VmDriveTile : Canvas
    {
        public string Letter;
        public long Total, Free;
        bool selected, hover;
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
        public double UsedPct { get { return Total > 0 ? (Total - Free) * 100.0 / Total : 0; } }

        public VmDriveTile(string letter, long total, long free)
        {
            Letter = letter.TrimEnd('\\', ':') + ":";
            Total = total; Free = free;
            Size = new Size(236, 96);
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, 12, 0);
            BackColor = Theme.Bg;
        }

        protected override void OnClick(EventArgs e) { Selected = !selected; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; Hq(g); g.Clear(Theme.Bg);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            Color fill = selected ? Theme.AccentSoft : Theme.Surface;
            Color border = selected ? Theme.Accent : (hover ? Theme.Faint : Theme.Border);
            using (GraphicsPath path = Util.RoundRect(r, 10))
            using (var b = new SolidBrush(fill)) using (var p = new Pen(border, selected ? 2f : 1f))
            { g.FillPath(b, path); g.DrawPath(p, path); }
            TextRenderer.DrawText(g, Letter, Theme.DriveFont, new Point(12, 8), Theme.Ink, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "VM drive", Theme.Small, new Point(10 + TextRenderer.MeasureText(Letter, Theme.DriveFont).Width, 13), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            var cb = new RectangleF(Width - 34, 14, 18, 18);
            using (GraphicsPath path = Util.RoundRect(cb, 5))
            {
                if (selected)
                {
                    using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                    using (var p = new Pen(Color.White, 2f))
                        g.DrawLines(p, new[] { new PointF(cb.X + 4, cb.Y + 9.5f), new PointF(cb.X + 7.5f, cb.Y + 13), new PointF(cb.X + 14, cb.Y + 5.5f) });
                }
                else using (var p = new Pen(Theme.Faint, 1.5f)) g.DrawPath(p, path);
            }
            if (Total > 0)
            {
                double pct = UsedPct;
                Color usage = Theme.UsageColor(pct);
                var track = new RectangleF(14, 52, Width - 28, 8);
                using (GraphicsPath path = Util.RoundRect(track, 4)) using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, path);
                var barR = new RectangleF(track.X, track.Y, (float)(track.Width * pct / 100.0), track.Height);
                if (barR.Width >= 1) using (GraphicsPath path = Util.RoundRect(barR, 4)) using (var b = new SolidBrush(usage)) g.FillPath(b, path);
                TextRenderer.DrawText(g, Util.Size(Free) + " free of " + Util.Size(Total), Theme.Small, new Point(12, 67), Theme.Muted, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, pct.ToString("0") + "% used", Theme.SmallBold, new Rectangle(0, 67, Width - 14, 18), usage == Theme.Accent ? Theme.Ink : usage, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            }
            else
            {
                TextRenderer.DrawText(g, "tick to re-scan", Theme.Small, new Point(12, 68), Theme.Faint, TextFormatFlags.NoPrefix);
            }
        }
    }

    class SemPlantRow
    {
        public string SiteKey, Site, Name, IniPath, FolderPath, Note;
        public long? FolderBytes, DatabaseBytes;
        public bool CanDelete;
    }

    static class SemCliRunner
    {
        static string Q(string value) { return "\"" + (value ?? "").Replace("\"", "\\\"") + "\""; }

        public static string Run(MachineSession session, string ini, string command, params string[] options)
        {
            string rest = "--ini " + Q(ini) + " " + command;
            if (options != null) foreach (string option in options) rest += " " + Q(option);
            if (session != null && session.IsRemote)
                return RemoteAgent.RunPlantUtility(session.Name, rest, null, null);

            string helper = Path.Combine(Application.StartupPath, "Delete Plant Utility CLI.exe");
            string script = Path.Combine(Application.StartupPath, "delete_plant_cli.py");
            bool useExe = File.Exists(helper);
            if (!useExe && !File.Exists(script))
                throw new FileNotFoundException("Build Delete Plant Utility CLI.exe and place it beside SpaceAnalyzer.exe.", helper);
            string output = Path.Combine(Path.GetTempPath(), "SA_sem_" + Guid.NewGuid().ToString("N") + ".json");
            var psi = new ProcessStartInfo
            {
                FileName = useExe ? helper : "python",
                Arguments = (useExe ? "" : Q(script) + " ") + "--ini " + Q(ini) + " --output " + Q(output) + " " + command,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = Application.StartupPath
            };
            if (options != null) foreach (string option in options) psi.Arguments += " " + Q(option);
            using (Process process = Process.Start(psi))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(30 * 60 * 1000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("The SEM site operation took too long.");
                }
                if (File.Exists(output))
                {
                    string result = File.ReadAllText(output);
                    try { File.Delete(output); } catch { }
                    return result;
                }
                throw new Exception("The SEM helper returned no result. " + (stderr.Length > 0 ? stderr : stdout));
            }
        }
    }

    class SemPlantsForm : Form
    {
        readonly MachineSession session;
        readonly DirNode scannedRoot;
        readonly ListView list;
        readonly ComboBox sortBy;
        readonly Label status, sharedInfo;
        readonly FlatBtn deleteButton;
        List<SemPlantRow> rows = new List<SemPlantRow>();
        bool loading;

        public SemPlantsForm(MachineSession machine, DirNode root)
        {
            session = machine; scannedRoot = root;
            Text = "SEM sites and plants - " + machine.Name;
            BackColor = Theme.Bg; ForeColor = Theme.Ink; Font = Theme.Body;
            Size = new Size(1060, 680); MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Bg, Padding = new Padding(20, 12, 20, 6) };
            var title = new Label { Text = "Sites and plants on " + machine.Name, Font = Theme.Title, ForeColor = Theme.Ink, AutoSize = true, Location = new Point(20, 10) };
            status = new Label { Text = "Reading discovered SEM site files...", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = false, Location = new Point(22, 48), Size = new Size(700, 28) };
            var sortLabel = new Label { Text = "Sort plants by", AutoSize = true, ForeColor = Theme.Muted, Location = new Point(720, 50) };
            sortBy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Location = new Point(805, 45) };
            sortBy.Items.AddRange(new object[] { "Folder space", "Database space", "Plant name" });
            sortBy.SelectedIndex = 0;
            sortBy.SelectedIndexChanged += (s, e) => FillRows();
            header.Controls.Add(title); header.Controls.Add(status); header.Controls.Add(sortLabel); header.Controls.Add(sortBy);

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
                HideSelection = false, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, BackColor = Theme.Surface, ForeColor = Theme.Ink };
            list.Columns.Add("Plant", 190); list.Columns.Add("Folder space", 120); list.Columns.Add("Database space", 135); list.Columns.Add("Plant folder", 390); list.Columns.Add("Status", 180);
            list.DoubleClick += (s, e) => ReviewSelected();

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Theme.Bg, Padding = new Padding(20, 4, 20, 8) };
            sharedInfo = new Label { Dock = DockStyle.Fill, Text = "Shared database storage will be shown here.", AutoEllipsis = true, ForeColor = Theme.Muted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft };
            deleteButton = new FlatBtn("Review and delete plant", BtnKind.Danger); deleteButton.Width = 205; deleteButton.Enabled = false;
            deleteButton.Click += (s, e) => ReviewSelected();
            var close = new FlatBtn("Close", BtnKind.Secondary); close.Click += (s, e) => Close();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
            flow.Controls.Add(close); flow.Controls.Add(deleteButton);
            footer.Controls.Add(sharedInfo); footer.Controls.Add(flow);
            list.SelectedIndexChanged += (s, e) => deleteButton.Enabled = !loading && list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is SemPlantRow && ((SemPlantRow)list.SelectedItems[0].Tag).CanDelete;

            Controls.Add(list); Controls.Add(footer); Controls.Add(header);
            Shown += (s, e) => LoadInventory();
        }

        void SafeInvoke(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
            catch { }
        }

        void LoadInventory()
        {
            if (loading) return;
            loading = true; deleteButton.Enabled = false; list.Items.Clear(); rows.Clear();
            status.Text = "Reading site details and database sizes...";
            var paths = (session.IniPaths ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var worker = new Thread(() =>
            {
                var found = new List<SemPlantRow>();
                var shared = new List<string>();
                var siteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string ini in paths)
                {
                    try
                    {
                        string json = SemCliRunner.Run(session, ini, "inventory");
                        Dictionary<string, object> data = AsDict(new JavaScriptSerializer().DeserializeObject(json));
                        if (!Bool(data, "ok")) throw new Exception(Value(data, "error"));
                        Dictionary<string, object> site = AsDict(Get(data, "site"));
                        string key = Value(site, "database") + "|" + Value(site, "server") + "|" + Value(site, "site_schema") + "|" + Value(site, "site_database");
                        if (siteKeys.Add(key))
                        {
                        string siteName = Value(site, "name") + " (" + Value(site, "database") + " - " + Value(site, "server") + ")";
                            foreach (object item in AsList(Get(data, "plants")))
                            {
                                Dictionary<string, object> plant = AsDict(item);
                                string path = Value(plant, "path");
                                found.Add(new SemPlantRow { SiteKey = key, Site = siteName,
                                    Name = Value(plant, "name"), IniPath = ini, FolderPath = path,
                                    FolderBytes = FindSize(scannedRoot, path), DatabaseBytes = Number(plant, "database_bytes"),
                                    CanDelete = true, Note = Value(data, "warnings") });
                            }
                            foreach (object item in AsList(Get(data, "shared_tablespaces")))
                            {
                                Dictionary<string, object> ts = AsDict(item);
                                shared.Add(siteName + ": " + Value(ts, "name") + " (" + Util.Size(Number(ts, "bytes") ?? 0) + ", shared by " + Value(ts, "owners") + " schemas)");
                            }
                            foreach (object item in AsList(Get(data, "shared_databases")))
                            {
                                Dictionary<string, object> db = AsDict(item);
                                shared.Add(siteName + ": shared database " + Value(db, "name") + " on " + Value(db, "server"));
                            }
                            foreach (object item in AsList(Get(data, "shared_schemas")))
                            {
                                Dictionary<string, object> schema = AsDict(item);
                                int count = AsList(Get(schema, "plants")).Count;
                                shared.Add(siteName + ": shared Oracle schema " + Value(schema, "name") +
                                    " (" + count + " plants; excluded from individual totals)");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        found.Add(new SemPlantRow { Site = "Unreadable site file", Name = Path.GetFileName(Path.GetDirectoryName(ini)),
                            IniPath = ini, FolderPath = ini, Note = ex.Message, CanDelete = false });
                    }
                }
                SafeInvoke((Action)(() =>
                {
                    rows = found; loading = false; FillRows();
                    status.Text = paths.Count == 0 ? "No smartplantv4.ini files were found on this machine." :
                        "Found " + paths.Count + " site file(s) and " + found.Count(r => r.CanDelete) + " plant(s).";
                    sharedInfo.Text = shared.Count == 0 ? "No shared database storage was reported." : string.Join("    |    ", shared);
                    deleteButton.Enabled = list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is SemPlantRow && ((SemPlantRow)list.SelectedItems[0].Tag).CanDelete;
                }));
            });
            worker.IsBackground = true; worker.Start();
        }

        void FillRows()
        {
            if (list == null) return;
            list.BeginUpdate(); list.Items.Clear(); list.Groups.Clear();
            IEnumerable<IGrouping<string, SemPlantRow>> groups = rows.GroupBy(r => string.IsNullOrEmpty(r.SiteKey) ? "errors" : r.SiteKey);
            foreach (IGrouping<string, SemPlantRow> group in groups)
            {
                string label = group.First().Site;
                var header = new ListViewGroup(label, HorizontalAlignment.Left);
                list.Groups.Add(header);
                IEnumerable<SemPlantRow> ordered = group;
                if (sortBy.SelectedIndex == 0) ordered = ordered.OrderByDescending(r => r.FolderBytes ?? -1);
                else if (sortBy.SelectedIndex == 1) ordered = ordered.OrderByDescending(r => r.DatabaseBytes ?? -1);
                else ordered = ordered.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
                foreach (SemPlantRow row in ordered)
                {
                    string folder = row.FolderBytes.HasValue ? Util.Size(row.FolderBytes.Value) : "Unavailable";
                    string db = row.DatabaseBytes.HasValue ? Util.Size(row.DatabaseBytes.Value) : "Unavailable";
                    string state = row.CanDelete ? (row.FolderBytes.HasValue ? "Ready to review" : "Folder not in scan") : row.Note;
                    var item = new ListViewItem(new[] { row.Name ?? "", folder, db, row.FolderPath ?? "", state }) { Tag = row, Group = header };
                    list.Items.Add(item);
                }
            }
            list.EndUpdate();
        }

        void ReviewSelected()
        {
            if (list.SelectedItems.Count != 1) return;
            SemPlantRow row = list.SelectedItems[0].Tag as SemPlantRow;
            if (row == null || !row.CanDelete) return;
            try
            {
                status.Text = "Building a fresh removal plan for " + row.Name + "...";
                string json = SemCliRunner.Run(session, row.IniPath, "plan", "--plant", row.Name);
                Dictionary<string, object> data = AsDict(new JavaScriptSerializer().DeserializeObject(json));
                if (!Bool(data, "ok")) throw new Exception(Value(data, "error"));
                Dictionary<string, object> plan = AsDict(Get(data, "plan"));
                string phrase = Value(data, "confirmation_phrase");
                var detail = new StringBuilder();
                foreach (object plantObj in AsList(Get(plan, "plants")))
                {
                    Dictionary<string, object> plant = AsDict(plantObj);
                    detail.AppendLine("Plant: " + Value(plant, "name"));
                    detail.AppendLine("Folder: " + Value(plant, "path"));
                    foreach (object targetObj in AsList(Get(plant, "targets")))
                    {
                        Dictionary<string, object> target = AsDict(targetObj);
                        string blocked = Value(target, "blocked");
                        detail.AppendLine("  " + (blocked.Length == 0 ? "Included" : "BLOCKED: " + blocked) + " - " + Value(target, "label") + " - " + Value(target, "name"));
                    }
                    foreach (object warning in AsList(Get(plant, "warnings"))) detail.AppendLine("Warning: " + Convert.ToString(warning));
                    foreach (object note in AsList(Get(plant, "notes"))) detail.AppendLine("Note: " + Convert.ToString(note));
                    detail.AppendLine();
                }
                using (var confirm = new SemDeleteConfirmForm(row.Name, phrase, detail.ToString()))
                {
                    if (confirm.ShowDialog(this) != DialogResult.OK) { status.Text = "Removal cancelled."; return; }
                }
                status.Text = "Removing " + row.Name + "...";
                json = SemCliRunner.Run(session, row.IniPath, "execute", "--plant", row.Name, "--confirm", phrase, "--acknowledge");
                data = AsDict(new JavaScriptSerializer().DeserializeObject(json));
                if (!Bool(data, "ok")) throw new Exception(Value(data, "error") + " " + json);
                MessageBox.Show(this, "Plant removal finished. Scan the machine again to refresh its folder-space figures.", "SEM plant removal", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LoadInventory();
            }
            catch (Exception ex)
            {
                status.Text = "Could not remove the plant.";
                MessageBox.Show(this, "The plant was not fully removed. Review the audit log and re-scan/re-plan before retrying.\n\n" + ex.Message,
                    "SEM plant removal", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static long? FindSize(DirNode root, string path)
        {
            if (root == null || string.IsNullOrWhiteSpace(path)) return null;
            string wanted = path.TrimEnd('\\');
            var stack = new Stack<DirNode>(); stack.Push(root);
            while (stack.Count > 0)
            {
                DirNode node = stack.Pop();
                if (!string.IsNullOrEmpty(node.FullPath) && node.FullPath.TrimEnd('\\').Equals(wanted, StringComparison.OrdinalIgnoreCase)) return node.Size;
                foreach (DirNode child in node.Children) stack.Push(child);
            }
            return null;
        }

        static object Get(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
        static Dictionary<string, object> AsDict(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static List<object> AsList(object o) { return o as object[] != null ? ((object[])o).ToList() : new List<object>(); }
        static string Value(Dictionary<string, object> d, string key) { object v = Get(d, key); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        static bool Bool(Dictionary<string, object> d, string key) { object v = Get(d, key); return v is bool && (bool)v; }
        static long? Number(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key); if (v == null) return null;
            long n; return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? (long?)n : null;
        }
    }

    class SemDeleteConfirmForm : Form
    {
        readonly TextBox typed;
        readonly FlatBtn confirm;
        readonly string phrase;
        public SemDeleteConfirmForm(string plant, string requiredPhrase, string details)
        {
            phrase = requiredPhrase;
            Text = "Review permanent plant removal"; BackColor = Theme.Bg; ForeColor = Theme.Ink; Font = Theme.Body;
            Size = new Size(760, 620); MinimumSize = new Size(640, 500); StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            var heading = new Label { Text = "This permanently removes " + plant, Font = Theme.Title, ForeColor = Theme.Danger, Dock = DockStyle.Top, Height = 54, Padding = new Padding(18, 12, 0, 0) };
            var warning = new Label { Text = "Review every listed folder and database item. Deletion cannot be undone.", ForeColor = Theme.Muted, Dock = DockStyle.Top, Height = 34, Padding = new Padding(20, 4, 0, 0) };
            var body = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = details, Font = Theme.Small };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 115, Padding = new Padding(18, 6, 18, 8), BackColor = Theme.Bg };
            var label = new Label { Text = "Type exactly: " + phrase, Dock = DockStyle.Top, Height = 25, ForeColor = Theme.Ink };
            typed = new TextBox { Dock = DockStyle.Top, Width = 400 };
            confirm = new FlatBtn("Delete permanently", BtnKind.Danger); confirm.Enabled = false;
            typed.TextChanged += (s, e) => confirm.Enabled = typed.Text.Equals(phrase, StringComparison.Ordinal);
            confirm.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            var cancel = new FlatBtn("Cancel", BtnKind.Secondary); cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, 7, 0, 0) };
            flow.Controls.Add(cancel); flow.Controls.Add(confirm);
            footer.Controls.Add(flow); footer.Controls.Add(typed); footer.Controls.Add(label);
            Controls.Add(body); Controls.Add(footer); Controls.Add(warning); Controls.Add(heading);
        }
    }

    class MainForm : Form
    {
        // Runs an action on the UI thread, but never crashes if the window isn't ready yet.
        void SafeInvoke(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch { }
        }

        FlowLayoutPanel drivesFlow;
        FlatBtn btnScan, btnStop, btnRefresh, btnEmptyBin, btnOwners, btnAzure, btnSchedule, btnSemPlants;
        Panel machineBar;
        FlowLayoutPanel machineTabs;
        List<MachineSession> sessions = new List<MachineSession>();
        MachineSession active;
        Queue<MachineSession> scanQueue = new Queue<MachineSession>();
        FlatBtn btnFolders, btnFiles, btnTypes, btnUsers;
        FlatBtn btnUp, btnOpenFolder, btnDeleteFolder;
        FlatBtn btnShowFile, btnDeleteFiles, btnExport;
        Label lblStatus, lblPath, lblSummary, lblFilesInfo;
        Panel folderView, filesView, typesView, usersView, progressStrip;
        DataGridView gridFolders, gridFiles, gridTypes, gridUsers;
        SplitContainer splitFolders, splitFiles, splitTypes, splitUsers;
        Treemap treemap;
        Bars bars, userBars;
        Donut donut;
        System.Windows.Forms.Timer uiTimer;

        Scanner scanner;
        DirNode root, current;
        List<DriveCard> driveCards = new List<DriveCard>();
        List<string> scannedDrives = new List<string>();
        string remoteHost;
        bool remoteMode;
        bool advReadOnly;
        List<AdvRow> remoteAdv;
        string remoteStatus = "";
        Dictionary<string, Color> extColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        bool scanning;
        int progressPhase;
        DateTime scanStart;
        string filesInfoBase = "";

        // Cleanup Advisor
        List<CleanupRule> rules;
        List<AdvisorRow> advisorRows = new List<AdvisorRow>();
        Panel advisorView;
        FlowLayoutPanel advisorFlow;
        SplitContainer splitAdvisor;
        Label lblAdvHead, lblAdvSub;
        FlatBtn btnAdvisor, btnClean, btnCheckAgain;
        Donut advisorDonut;
        Advisor advisor;
        System.Windows.Forms.Timer advTimer;
        bool advBusy, advCleaning, reanalyzePending;

        // Search / filter
        SearchBox search;
        FilterSpec filter = new FilterSpec();
        System.Windows.Forms.Timer filterTimer;
        CheckBox chkFast;

        // Duplicates
        Panel dupView;
        SplitContainer splitDups;
        DataGridView gridDups;
        Bars dupBars;
        Label lblDupInfo;
        FlatBtn btnDups, btnFindDups, btnDupSelect, btnDupDelete, btnDupShow;
        List<DupGroup> dupGroups;
        Dictionary<FileEntry, Color> dupColors = new Dictionary<FileEntry, Color>();
        DupFinder dupFinder;
        System.Windows.Forms.Timer dupTimer;
        bool dupBusy;

        public MainForm()
        {
            Text = "Space Analyzer - " + Environment.MachineName;
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            Font = Theme.Body;
            Size = new Size(1320, 860);
            MinimumSize = new Size(1080, 700);
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            rules = CleanupRules.Build();
            BuildUi();

            filterTimer = new System.Windows.Forms.Timer { Interval = 350 };
            filterTimer.Tick += (s, e) => { filterTimer.Stop(); ApplyFilter(); };
            dupTimer = new System.Windows.Forms.Timer { Interval = 200 };
            dupTimer.Tick += (s, e) => OnDupTick();

            advTimer = new System.Windows.Forms.Timer { Interval = 150 };
            advTimer.Tick += (s, e) => OnAdvisorTick();

            uiTimer = new System.Windows.Forms.Timer { Interval = 120 };
            uiTimer.Tick += (s, e) => OnScanTick();

            Shown += (s, e) =>
            {
                active = LocalSession();
                RebuildMachineTabs();
                LoadDrives();
                ShowView(3);
                StartAnalyze();
            };
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5 && btnScan.Enabled) { StartScan(); e.Handled = true; }
            };
        }

        // ================= building the window =================
        void BuildUi()
        {
            // ---- main area ----
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 2, 20, 12), BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var tabs = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Theme.Bg };

            btnAdvisor = new FlatBtn("Cleanup advisor", BtnKind.Tab);
            btnFolders = new FlatBtn("Biggest folders", BtnKind.Tab);
            btnFiles = new FlatBtn("Biggest files", BtnKind.Tab);
            btnTypes = new FlatBtn("File types", BtnKind.Tab);
            btnUsers = new FlatBtn("By user", BtnKind.Tab);
            btnDups = new FlatBtn("Duplicates", BtnKind.Tab);
            btnAdvisor.Location = new Point(0, 8);
            btnFolders.Location = new Point(btnAdvisor.Right + 6, 8);
            btnFiles.Location = new Point(btnFolders.Right + 6, 8);
            btnTypes.Location = new Point(btnFiles.Right + 6, 8);
            btnDups.Location = new Point(btnTypes.Right + 6, 8);
            btnDups.Click += (s, e) => ShowView(4);
            btnAdvisor.Click += (s, e) => ShowView(3);
            btnFolders.Click += (s, e) => ShowView(0);
            btnFiles.Click += (s, e) => ShowView(1);
            btnTypes.Click += (s, e) => ShowView(2);
            btnUsers.Location = new Point(btnTypes.Right + 6, 8);
            btnUsers.Click += (s, e) => ShowView(5);
            search = new SearchBox("Filter lists: .bak   older than 1 year   >1gb   name");
            search.Width = 330;
            search.Box.TextChanged += (s, e) => { filterTimer.Stop(); filterTimer.Start(); };
            new ToolTip().SetToolTip(search.Box,
                "Type to narrow the lists. Examples:\n" +
                ".bak  or  .iso .zip      file types\n" +
                "older than 1 year        not changed for a year (also: days, weeks, months)\n" +
                ">1gb  or  under 50mb     size\n" +
                "setup                    name or folder contains this word\n" +
                "Combine them:  .bak older than 90 days >500mb");
            var searchHolder = new Panel { Dock = DockStyle.Right, Width = 330, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 8) };
            search.Dock = DockStyle.Fill;
            searchHolder.Controls.Add(search);
            tabs.Controls.Add(searchHolder);
            tabs.Controls.Add(btnAdvisor);
            tabs.Controls.Add(btnFolders);
            tabs.Controls.Add(btnFiles);
            tabs.Controls.Add(btnTypes);
            tabs.Controls.Add(btnUsers);

            BuildFolderView();
            BuildFilesView();
            BuildTypesView();
            BuildUsersView();
            BuildAdvisorView();
            BuildDupView();
            content.Controls.Add(advisorView);
            content.Controls.Add(dupView);
            content.Controls.Add(folderView);
            content.Controls.Add(filesView);
            content.Controls.Add(typesView);
            content.Controls.Add(usersView);

            body.Controls.Add(content);
            body.Controls.Add(tabs);

            // ---- status bar ----
            var status = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Theme.Surface };
            progressStrip = new Panel { Dock = DockStyle.Top, Height = 3, BackColor = Theme.Surface };
            progressStrip.Paint += PaintProgress;
            lblStatus = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 0, 0, 0),
                ForeColor = Theme.Muted, Font = Theme.Small, AutoEllipsis = true,
                Text = "Ready. Select one or more drives, then click Scan drives (or press F5)."
            };
            var logLink = new LinkLabel
            {
                Dock = DockStyle.Right, Width = 150, Text = "Open deletion log", TextAlign = ContentAlignment.MiddleCenter,
                Font = Theme.Small, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Darken(Theme.Accent, 0.2f),
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            logLink.LinkClicked += (s, e) => OpenLog();
            var dbgLink = new LinkLabel
            {
                Dock = DockStyle.Right, Width = 150, Text = "Open debug log", TextAlign = ContentAlignment.MiddleCenter,
                Font = Theme.Small, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Darken(Theme.Accent, 0.2f),
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            dbgLink.LinkClicked += (s, e) =>
            {
                try { if (File.Exists(Log.Path1)) Process.Start(Log.Path1); else MessageBox.Show(this, "No debug log yet.", "Debug log", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                catch { Util.OpenInExplorer(Log.Path1, true); }
            };
            status.Controls.Add(lblStatus);
            status.Controls.Add(logLink);
            status.Controls.Add(dbgLink);
            status.Controls.Add(progressStrip);

            // ---- drive strip ----
            var drivesArea = new Panel { Dock = DockStyle.Top, Height = 116, Padding = new Padding(20, 0, 20, 8), BackColor = Theme.Bg };
            drivesFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg,
                Padding = new Padding(0, 2, 0, 0)
            };
            drivesArea.Controls.Add(drivesFlow);

            // ---- header ----
            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.Bg };
            var lblTitle = new Label
            {
                Text = "Space Analyzer", Font = Theme.Title, ForeColor = Theme.Ink,
                AutoSize = true, Location = new Point(18, 14)
            };
            lblSummary = new Label
            {
                Text = "Find what is filling the disks on " + Environment.MachineName + " and clean it up safely.",
                Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true,
                Location = new Point(20, 46), Size = new Size(500, 22)
            };
            chkFast = new CheckBox
            {
                Text = "Fast scan", Checked = true, AutoSize = false, Size = new Size(96, 34),
                Font = Theme.Body, ForeColor = Theme.Ink, BackColor = Theme.Bg, Margin = new Padding(8, 0, 0, 0)
            };
            new ToolTip().SetToolTip(chkFast,
                "Reads the NTFS disk index directly, like WizTree. A full drive takes seconds.\n" +
                "If a drive can't be read this way, the normal scan is used automatically.");
            btnScan = new FlatBtn("Scan drives", BtnKind.Primary);
            btnStop = new FlatBtn("Stop", BtnKind.Secondary);
            btnRefresh = new FlatBtn("Refresh drive list", BtnKind.Secondary);
            btnAzure = new FlatBtn("Network VMs", BtnKind.Secondary);
            btnAzure.Click += (s, e) => { using (var f = new NetworkForm(this)) f.ShowDialog(this); };
            btnSemPlants = new FlatBtn("SEM plants", BtnKind.Secondary);
            btnSemPlants.Click += (s, e) =>
            {
                if (active == null || !active.IniScanFinished) return;
                using (var f = new SemPlantsForm(active, root)) f.ShowDialog(this);
            };
            btnSchedule = new FlatBtn("Schedule", BtnKind.Secondary);
            btnSchedule.Click += (s, e) => { using (var f = new ScheduleForm()) f.ShowDialog(this); };
            btnEmptyBin = new FlatBtn("Recycle Bin", BtnKind.Secondary);
            btnScan.Click += (s, e) => StartScan();
            btnStop.Click += (s, e) =>
            {
                if (scanner != null) { scanner.Cancel = true; lblStatus.Text = "Stopping scan..."; }
            };
            btnRefresh.Click += (s, e) => LoadDrives();
            btnEmptyBin.Click += (s, e) => EmptyBin();
            FlowLayoutPanel actions = RightFlow(22, btnScan, chkFast, btnStop, btnRefresh, btnEmptyBin, btnSemPlants, btnAzure, btnSchedule);
            actions.Padding = new Padding(0, 22, 20, 0);
            actions.Width += 20;
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSummary);
            header.Controls.Add(actions);
            header.Resize += (s, e) => lblSummary.Width = Math.Max(100, header.Width - actions.Width - 40);

            // ---- machine bar (one tab per scanned machine) ----
            machineBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg, Padding = new Padding(20, 4, 20, 4) };
            machineTabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg };
            var lblViewing = new Label { Dock = DockStyle.Left, AutoSize = false, Width = 70, Text = "Viewing:", TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.Small };
            machineBar.Controls.Add(machineTabs);
            machineBar.Controls.Add(lblViewing);

            // Docking order: the control added last sits at the very top/bottom edge.
            Controls.Add(body);
            Controls.Add(status);
            Controls.Add(drivesArea);
            Controls.Add(machineBar);
            Controls.Add(header);
            UpdateButtons();
        }

        // ============ machine sessions ============
        MachineSession LocalSession()
        {
            MachineSession m = sessions.FirstOrDefault(x => !x.IsRemote);
            if (m == null)
            {
                m = new MachineSession { Name = "This computer (" + Environment.MachineName + ")", IsRemote = false };
                sessions.Insert(0, m);
            }
            return m;
        }

        MachineSession GetOrAddRemote(string host)
        {
            MachineSession m = sessions.FirstOrDefault(x => x.IsRemote && x.Name.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (m == null) { m = new MachineSession { Name = host, IsRemote = true }; sessions.Add(m); }
            return m;
        }

        void RebuildMachineTabs()
        {
            machineTabs.Controls.Clear();
            foreach (MachineSession m in sessions)
            {
                MachineSession ms = m;
                var tab = new FlatBtn(TabLabel(m), BtnKind.Tab);
                tab.Active = (m == active);
                tab.Click += (s, e) => SwitchTo(ms);
                machineTabs.Controls.Add(tab);
                if (m.IsRemote)
                {
                    var close = new FlatBtn("x", BtnKind.Tab) { Width = 26 };
                    close.Click += (s, e) => CloseSession(ms);
                    machineTabs.Controls.Add(close);
                }
            }
        }

        string TabLabel(MachineSession m)
        {
            string tag = m == active && scanning ? "  (scanning)" : (!m.HasResult && m != active ? "  (queued)" : "");
            return (m.IsRemote ? m.Name : "This computer") + tag;
        }

        // Save whatever is on screen into the active session, so we can restore it later.
        void SaveActive()
        {
            if (active == null) return;
            active.Scanner = scanner; active.Root = root; active.Current = current;
            active.Adv = remoteAdv; active.Files = scanner != null ? scanner.Files : 0;
            active.Bytes = scanner != null ? scanner.Bytes : 0;
            if (scanner != null) active.IniPaths = new List<string>(scanner.IniPaths);
            active.HasResult = root != null;
        }

        void StoreActiveResult()
        {
            if (active == null) return;
            active.Scanner = scanner; active.Root = root; active.Current = current;
            active.Adv = remoteAdv; active.HasResult = root != null;
            active.Files = scanner != null ? scanner.Files : 0;
            active.Bytes = scanner != null ? scanner.Bytes : 0;
            if (scanner != null) active.IniPaths = new List<string>(scanner.IniPaths);
        }

        void RunNextQueued()
        {
            if (scanning || scanQueue.Count == 0) return;
            MachineSession next = scanQueue.Dequeue();
            if (next == null || !next.IsRemote) { RunNextQueued(); return; }
            SaveActive();
            active = next;
            RebuildMachineTabs();
            ScanActiveRemote();
        }

        void ScanActiveRemote()
        {
            remoteMode = true; remoteHost = active.Name;
            ScanRemoteAgent(active.Name, active.DriveLetters, active.UncRoots);
        }

        void SwitchTo(MachineSession m)
        {
            if (m == active) return;
            if (scanning) { MessageBox.Show(this, "Please wait for the current scan to finish before switching.", "Scanning", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SaveActive();
            active = m;
            remoteMode = m.IsRemote;
            remoteHost = m.IsRemote ? m.Name : null;
            // Reset advisor state so one machine's numbers never linger on another.
            ResetAdvisorRules();
            if (m.HasResult)
            {
                scanner = m.Scanner; root = m.Root; current = m.Current; remoteAdv = m.Adv;
                advReadOnly = m.IsRemote;
                FillTypes(); FillFiles();
                NavigateTo(current ?? root);
                FillDuplicates();
                UpdateSummary();
                lblStatus.Text = "Viewing " + m.Name + ".";
                if (m.IsRemote) ApplyRemoteAdvisor();
                else { advReadOnly = false; analyzedOnce = false; StartAnalyze(true); }   // re-check this computer locally
            }
            else
            {
                scanner = null; root = null; current = null;
                advReadOnly = m.IsRemote;
                lblSummary.Text = m.Name + " - not scanned yet.";
                lblStatus.Text = "Viewing " + m.Name + " (queued or not scanned).";
                RefreshAdvisor();
            }
            LoadDrives();
            RebuildMachineTabs();
            UpdateButtons();
            ShowView(0);
        }

        void CloseSession(MachineSession m)
        {
            if (scanning && m == active) { MessageBox.Show(this, "Can't close a machine while it's scanning.", "Scanning", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            sessions.Remove(m);
            if (active == m) { active = null; SwitchTo(LocalSession()); }
            else RebuildMachineTabs();
        }

        FlowLayoutPanel RightFlow(int top, params Control[] ctrls)
        {
            var f = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
                BackColor = Theme.Bg, Padding = new Padding(0, top, 0, 0)
            };
            int w = 4;
            foreach (Control c in ctrls) { f.Controls.Add(c); w += c.Width + c.Margin.Horizontal; }
            f.Width = w;
            return f;
        }

        void BuildFolderView()
        {
            folderView = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            var crumb = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Bg };
            btnUp = new FlatBtn("Up one level", BtnKind.Secondary);
            btnUp.Location = new Point(0, 6);
            btnUp.Click += (s, e) => GoUp();
            var upHolder = new Panel { Dock = DockStyle.Left, Width = btnUp.Width + 4, BackColor = Theme.Bg };
            upHolder.Controls.Add(btnUp);
            lblPath = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.BodyBold,
                ForeColor = Theme.Ink, AutoEllipsis = true, Padding = new Padding(10, 0, 0, 6), Text = "No scan yet"
            };
            btnDeleteFolder = new FlatBtn("Move to Recycle Bin", BtnKind.Danger);
            btnOpenFolder = new FlatBtn("Open in Explorer", BtnKind.Secondary);
            btnDeleteFolder.Click += (s, e) => DeleteSelectedFolders();
            btnOpenFolder.Click += (s, e) => OpenSelectedFolder();
            crumb.Controls.Add(lblPath);
            crumb.Controls.Add(upHolder);
            crumb.Controls.Add(RightFlow(6, btnDeleteFolder, btnOpenFolder));

            splitFolders = MakeSplit();

            var cardList = new Card { Title = "Folders", Subtitle = "Double-click a folder to open it, or the files row to list its files" };
            gridFolders = GridStyle.Make();
            gridFolders.Columns.Add(GridStyle.Col("name", "Name", 0, 60, false));
            gridFolders.Columns.Add(GridStyle.Col("size", "Size", 96, 0, true));
            gridFolders.Columns.Add(GridStyle.Col("share", "Share of this folder", 170, 0, false));
            gridFolders.Columns.Add(GridStyle.Col("files", "Files", 84, 0, true));
            gridFolders.CellPainting += PaintCell;
            gridFolders.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                RowItem ri = gridFolders.Rows[e.RowIndex].Tag as RowItem;
                if (ri != null && ri.IsFiles) { ShowFilesInCurrentFolder(); return; }
                OpenRow(ri);
            };
            gridFolders.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { DeleteSelectedFolders(); e.Handled = true; }
                else if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter && gridFolders.CurrentRow != null)
                {
                    OpenRow(gridFolders.CurrentRow.Tag as RowItem);
                    e.Handled = true;
                }
            };
            AttachMenu(gridFolders, false);
            cardList.Controls.Add(gridFolders);

            var cardMap = new Card { Title = "Space map", Subtitle = "Bigger box means more space. Click a folder box to open it." };
            treemap = new Treemap { Dock = DockStyle.Fill };
            treemap.ItemClicked += it => { if (it.Clickable) OpenRow(it.Tag as RowItem); };
            cardMap.Controls.Add(treemap);

            splitFolders.Panel1.Controls.Add(cardList);
            splitFolders.Panel2.Controls.Add(cardMap);

            folderView.Controls.Add(splitFolders);
            folderView.Controls.Add(crumb);
        }

        void BuildFilesView()
        {
            filesView = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };

            var bar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Bg };
            lblFilesInfo = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted,
                Padding = new Padding(2, 0, 0, 6), AutoEllipsis = true, Text = "Scan a drive to list its largest files."
            };
            btnExport = new FlatBtn("Export report", BtnKind.Secondary);
            btnDeleteFiles = new FlatBtn("Move to Recycle Bin", BtnKind.Danger);
            btnShowFile = new FlatBtn("Show in Explorer", BtnKind.Secondary);
            btnExport.Click += (s, e) => ExportReport();
            btnDeleteFiles.Click += (s, e) => DeleteSelectedFiles();
            btnShowFile.Click += (s, e) => ShowSelectedFile();
            bar.Controls.Add(lblFilesInfo);
            bar.Controls.Add(RightFlow(6, btnExport, btnDeleteFiles, btnShowFile));

            splitFiles = MakeSplit();

            var cardList = new Card { Title = "Largest files", Subtitle = "Ctrl+click or Shift+click to select several" };
            gridFiles = GridStyle.Make();
            gridFiles.Columns.Add(GridStyle.Col("name", "Name", 0, 38, false));
            gridFiles.Columns.Add(GridStyle.Col("size", "Size", 92, 0, true));
            gridFiles.Columns.Add(GridStyle.Col("type", "Type", 78, 0, false));
            gridFiles.Columns.Add(GridStyle.Col("modified", "Last changed", 128, 0, false));
            gridFiles.Columns.Add(GridStyle.Col("folder", "Folder", 0, 62, false));
            gridFiles.CellPainting += PaintCell;
            gridFiles.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) ShowSelectedFile(); };
            gridFiles.SelectionChanged += (s, e) => UpdateFilesInfo();
            gridFiles.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { DeleteSelectedFiles(); e.Handled = true; } };
            AttachMenu(gridFiles, true);
            cardList.Controls.Add(gridFiles);

            var cardBars = new Card { Title = "Top 15 files", Subtitle = "Colored by file type" };
            bars = new Bars { Dock = DockStyle.Fill };
            cardBars.Controls.Add(bars);

            splitFiles.Panel1.Controls.Add(cardList);
            splitFiles.Panel2.Controls.Add(cardBars);
            filesView.Controls.Add(splitFiles);
            filesView.Controls.Add(bar);
        }

        void BuildTypesView()
        {
            typesView = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            var spacer = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Bg };

            splitTypes = MakeSplit();
            var cardList = new Card { Title = "File types", Subtitle = "Which kinds of files use the most space" };
            gridTypes = GridStyle.Make();
            gridTypes.Columns.Add(GridStyle.Col("name", "Type", 0, 40, false));
            gridTypes.Columns.Add(GridStyle.Col("size", "Size", 96, 0, true));
            gridTypes.Columns.Add(GridStyle.Col("share", "Share of scanned space", 180, 0, false));
            gridTypes.Columns.Add(GridStyle.Col("files", "Files", 90, 0, true));
            gridTypes.CellPainting += PaintCell;
            cardList.Controls.Add(gridTypes);

            var cardDonut = new Card { Title = "Space by file type" };
            donut = new Donut { Dock = DockStyle.Fill };
            cardDonut.Controls.Add(donut);

            splitTypes.Panel1.Controls.Add(cardList);
            splitTypes.Panel2.Controls.Add(cardDonut);
            typesView.Controls.Add(splitTypes);
            typesView.Controls.Add(spacer);
        }

        // ===================== By user view =====================
        int userAgeDays = 180;

        void BuildUsersView()
        {
            usersView = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };

            var bar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Bg };
            var lbl = new Label { Dock = DockStyle.Left, AutoSize = false, Width = 420, Text = "Who is using space, from user profiles and team folders.", TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Padding = new Padding(2, 0, 0, 6) };
            var ageLbl = new Label { Dock = DockStyle.Left, AutoSize = true, Text = "Old = older than", ForeColor = Theme.Ink, Padding = new Padding(10, 14, 0, 0) };
            var ageBox = new NumericUpDown { Dock = DockStyle.Left, Width = 60, Minimum = 30, Maximum = 3650, Value = 180, Increment = 30, Margin = new Padding(6) };
            ageBox.ValueChanged += (s, e) => { userAgeDays = (int)ageBox.Value; };
            var ageUnit = new Label { Dock = DockStyle.Left, AutoSize = true, Text = "days", ForeColor = Theme.Muted, Padding = new Padding(4, 14, 0, 0) };
            var btnFolders2 = new FlatBtn("Team folders...", BtnKind.Secondary);
            btnFolders2.Click += (s, e) => EditUserFolders();
            var btnGo = new FlatBtn("Analyze users", BtnKind.Primary); btnGo.Width = 140;
            btnGo.Click += (s, e) => AnalyzeUsers();
            bar.Controls.Add(ageUnit); bar.Controls.Add(ageBox); bar.Controls.Add(ageLbl); bar.Controls.Add(lbl);
            bar.Controls.Add(RightFlow(6, btnGo, btnFolders2));

            splitUsers = MakeSplit();
            var cardList = new Card { Title = "People", Subtitle = "Ranked by how much old data they own" };
            gridUsers = GridStyle.Make();
            gridUsers.Columns.Add(GridStyle.Col("user", "User", 0, 40, false));
            gridUsers.Columns.Add(GridStyle.Col("old", "Old data", 110, 0, true));
            gridUsers.Columns.Add(GridStyle.Col("total", "Total data", 110, 0, true));
            gridUsers.Columns.Add(GridStyle.Col("oldfiles", "Old files", 90, 0, true));
            gridUsers.Columns.Add(GridStyle.Col("where", "Where", 0, 50, false));
            gridUsers.CellPainting += PaintCell;
            cardList.Controls.Add(gridUsers);

            var cardBars = new Card { Title = "Most old data by user", Subtitle = "Top 15" };
            userBars = new Bars { Dock = DockStyle.Fill };
            userBars.EmptyText = "Click Analyze users after a scan.";
            cardBars.Controls.Add(userBars);

            splitUsers.Panel1.Controls.Add(cardList);
            splitUsers.Panel2.Controls.Add(cardBars);
            usersView.Controls.Add(splitUsers);
            usersView.Controls.Add(bar);
        }

        void EditUserFolders()
        {
            UserFolders.EnsureFile();
            try { System.Diagnostics.Process.Start(UserFolders.Path1); }
            catch { Util.OpenInExplorer(UserFolders.Path1, true); }
            lblStatus.Text = "List parent folders of team folders (one per line), save, then click Analyze users.";
        }

        void AnalyzeUsers()
        {
            if (root == null) { MessageBox.Show(this, "Scan a machine first.", "By user", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var result = UserAnalyzer.Analyze(root, scanner, userAgeDays);
            gridUsers.SuspendLayout();
            gridUsers.Rows.Clear();
            long maxOld = result.Count > 0 ? Math.Max(1, result.Max(u => u.OldSize)) : 1;
            int ci = 0;
            foreach (UserStat u in result)
            {
                int i = gridUsers.Rows.Add(u.Name, Util.Size(u.OldSize), Util.Size(u.TotalSize), u.OldFiles.ToString("N0"), u.Where);
                gridUsers.Rows[i].Tag = Theme.Pick(ci++);
            }
            gridUsers.ResumeLayout();
            gridUsers.ClearSelection();

            userBars.SetData(result.Take(15).Select((u, idx) => new Seg
            {
                Label = u.Name, Detail = u.Name + " - " + u.Where + "\n" + Util.Size(u.OldSize) + " old of " + Util.Size(u.TotalSize),
                Value = u.OldSize, Color = Theme.Pick(idx)
            }).ToList());

            long totalOld = result.Sum(u => u.OldSize);
            lblStatus.Text = string.Format("Found {0} user(s). Together they have {1} in files older than {2} days.",
                result.Count, Util.Size(totalOld), userAgeDays);
        }


        // ================= Duplicates view =================
        void BuildDupView()
        {
            dupView = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };

            var bar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Bg };
            lblDupInfo = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted,
                Padding = new Padding(2, 0, 0, 6), AutoEllipsis = true
            };
            btnFindDups = new FlatBtn("Find duplicates", BtnKind.Primary);
            btnFindDups.Width = 150;
            btnDupSelect = new FlatBtn("Keep newest, select the rest", BtnKind.Secondary);
            btnDupDelete = new FlatBtn("Move to Recycle Bin", BtnKind.Danger);
            btnDupShow = new FlatBtn("Show in Explorer", BtnKind.Secondary);
            btnFindDups.Click += (s, e) => { if (dupBusy) { if (dupFinder != null) dupFinder.Cancel = true; } else StartDupSearch(); };
            btnDupSelect.Click += (s, e) => SelectAllButNewest();
            btnDupDelete.Click += (s, e) => DeleteSelectedDups();
            btnDupShow.Click += (s, e) =>
            {
                List<FileEntry> sel = SelectedDupFiles();
                if (sel.Count > 0) Util.OpenInExplorer(sel[0].FullPath, true);
            };
            bar.Controls.Add(lblDupInfo);
            bar.Controls.Add(RightFlow(6, btnDupDelete, btnDupShow, btnDupSelect, btnFindDups));

            splitDups = MakeSplit();
            var cardList = new Card { Title = "Duplicate files", Subtitle = "Each group is the same file saved in several places" };
            gridDups = GridStyle.Make();
            gridDups.Columns.Add(GridStyle.Col("name", "Name", 0, 38, false));
            gridDups.Columns.Add(GridStyle.Col("size", "Size", 110, 0, true));
            gridDups.Columns.Add(GridStyle.Col("modified", "Last changed", 128, 0, false));
            gridDups.Columns.Add(GridStyle.Col("folder", "Folder", 0, 62, false));
            gridDups.CellPainting += PaintCell;
            gridDups.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                FileEntry f = gridDups.Rows[e.RowIndex].Tag as FileEntry;
                if (f != null) Util.OpenInExplorer(f.FullPath, true);
            };
            gridDups.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { DeleteSelectedDups(); e.Handled = true; } };
            cardList.Controls.Add(gridDups);

            var cardBars = new Card { Title = "Most wasted space", Subtitle = "Space you get back by keeping one copy" };
            dupBars = new Bars { Dock = DockStyle.Fill };
            dupBars.EmptyText = "Click Find duplicates to see where space is wasted.";
            cardBars.Controls.Add(dupBars);

            splitDups.Panel1.Controls.Add(cardList);
            splitDups.Panel2.Controls.Add(cardBars);
            dupView.Controls.Add(splitDups);
            dupView.Controls.Add(bar);
            FillDuplicates();
        }

        void StartDupSearch()
        {
            if (root == null || scanner == null)
            {
                MessageBox.Show(this, "Click Scan drives first. Duplicates are searched among the files that scan finds.",
                    "Duplicates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var files = new List<FileEntry>(scanner.BigFiles);
            DupFinder df = new DupFinder();
            dupFinder = df;
            dupBusy = true;
            dupGroups = null;
            FillDuplicates();
            UpdateButtons();

            var t = new Thread(() =>
            {
                List<DupGroup> result = null;
                try { result = df.Find(files); } catch { }
                try { SafeInvoke((Action)(() => DupSearchFinished(df, result))); } catch { }
            });
            t.IsBackground = true;
            t.Start();
            dupTimer.Start();
        }

        void OnDupTick()
        {
            if (!scanning && !advBusy)
            {
                progressPhase++;
                progressStrip.Invalidate();
            }
            DupFinder df = dupFinder;
            if (df == null) return;
            long total = Interlocked.Read(ref df.BytesToCheck);
            long done = Interlocked.Read(ref df.BytesChecked);
            lblDupInfo.Text = df.Stage == "Comparing file contents" && total > 0
                ? string.Format("Comparing file contents... {0} of {1} checked ({2:0}%).", Util.Size(done), Util.Size(total), done * 100.0 / total)
                : "Looking for files with the same size and content...";
        }

        void DupSearchFinished(DupFinder df, List<DupGroup> result)
        {
            dupTimer.Stop();
            dupBusy = false;
            progressStrip.Invalidate();
            dupGroups = df.Cancel ? null : (result ?? new List<DupGroup>());
            FillDuplicates();
            if (df.Cancel) lblDupInfo.Text = "Duplicate check stopped. Click Find duplicates to start again.";
            UpdateButtons();
        }

        void FillDuplicates()
        {
            return;   // Duplicates feature removed from the UI
#pragma warning disable 0162
            if (gridDups == null) return;
            gridDups.SuspendLayout();
            gridDups.Rows.Clear();
            dupColors.Clear();
            if (dupBusy) { gridDups.ResumeLayout(); return; }

            if (dupGroups == null)
            {
                lblDupInfo.Text = root == null
                    ? "Click Scan drives first. Duplicates are searched among files larger than 10 MB."
                    : "Click Find duplicates to compare every file larger than 10 MB. Windows system folders are skipped.";
                dupBars.SetData(new List<Seg>());
                gridDups.ResumeLayout();
                return;
            }

            List<DupGroup> groups = dupGroups
                .Where(g => g.Files.Count > 1 && (filter.IsEmpty || g.Files.Any(filter.MatchFile)))
                .OrderByDescending(g => g.Wasted).ToList();

            int ci = 0, rowCount = 0;
            foreach (DupGroup g in groups)
            {
                if (rowCount > 4000) break;
                g.Color = Theme.Pick(ci++);
                int h = gridDups.Rows.Add(string.Format("{0} copies of {1}", g.Files.Count, g.Files[0].Name),
                    Util.Size(g.Size) + " each", "", Util.Size(g.Wasted) + " can be freed");
                DataGridViewRow hr = gridDups.Rows[h];
                hr.Tag = g;
                hr.DefaultCellStyle.BackColor = Theme.SurfaceAlt;
                hr.DefaultCellStyle.SelectionBackColor = Theme.SurfaceAlt;
                hr.DefaultCellStyle.SelectionForeColor = Theme.Ink;
                hr.DefaultCellStyle.Font = Theme.BodyBold;
                rowCount++;
                foreach (FileEntry f in g.Files.OrderByDescending(x => x.Modified))
                {
                    int i = gridDups.Rows.Add(f.Name, Util.Size(f.Size),
                        f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("yyyy-MM-dd HH:mm"), f.Folder.FullPath);
                    gridDups.Rows[i].Tag = f;
                    dupColors[f] = g.Color;
                    rowCount++;
                }
            }
            gridDups.ResumeLayout();
            gridDups.ClearSelection();

            long wasted = groups.Sum(g => g.Wasted);
            lblDupInfo.Text = groups.Count == 0
                ? (filter.IsEmpty ? "No duplicate files found. Nice and tidy." : "No duplicates match your filter.")
                : string.Format("{0:N0} groups of duplicates. Keeping one copy of each frees {1}.", groups.Count, Util.Size(wasted));
            dupBars.SetData(groups.Take(15).Select(g => new Seg
            {
                Label = g.Files[0].Name,
                Detail = g.Files.Count + " copies, " + Util.Size(g.Size) + " each\n" + string.Join("\n", g.Files.Take(5).Select(f => f.FullPath)),
                Value = g.Wasted, Color = g.Color
            }).ToList());
        }

        List<FileEntry> SelectedDupFiles()
        {
            return gridDups.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as FileEntry).Where(f => f != null).ToList();
        }

        void SelectAllButNewest()
        {
            gridDups.ClearSelection();
            var keep = new HashSet<FileEntry>();
            foreach (DupGroup g in dupGroups ?? new List<DupGroup>())
                if (g.Files.Count > 0) keep.Add(g.Files.OrderByDescending(f => f.Modified).First());
            foreach (DataGridViewRow r in gridDups.Rows)
            {
                FileEntry f = r.Tag as FileEntry;
                if (f != null && !keep.Contains(f)) r.Selected = true;
            }
            List<FileEntry> sel = SelectedDupFiles();
            lblDupInfo.Text = string.Format("Selected {0:N0} older copies ({1}). The newest copy in each group is kept.",
                sel.Count, Util.Size(sel.Sum(f => f.Size)));
        }

        void DeleteSelectedDups()
        {
            if (remoteMode) { MessageBox.Show(this, "Duplicate finding isn't available on remote VM scans yet.", "Remote scan", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (dupGroups == null || dupBusy || scanning || advCleaning) return;
            List<FileEntry> sel = SelectedDupFiles();
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "Select the copies you want to remove first, or click Keep newest, select the rest.",
                    "Duplicates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var selSet = new HashSet<FileEntry>(sel);
            foreach (DupGroup g in dupGroups)
            {
                if (g.Files.Count > 0 && g.Files.All(selSet.Contains))
                {
                    MessageBox.Show(this, "You selected every copy of " + g.Files[0].Name +
                        ". Keep at least one copy, then try again.", "Keep one copy",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            string msg = string.Format("Move {0} duplicate file(s), {1} in total, to the Recycle Bin?\n\n{2}{3}\n\n" +
                "One copy of each file is kept. You can restore them from the Recycle Bin until it is emptied.",
                sel.Count, Util.Size(sel.Sum(f => f.Size)), string.Join("\n", sel.Take(8).Select(f => f.FullPath)),
                sel.Count > 8 ? "\n...and " + (sel.Count - 8) + " more" : "");
            if (MessageBox.Show(this, msg, "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            Recycler.MoveToRecycleBin(this, sel.Select(f => f.FullPath).ToList());
            Cursor = Cursors.Default;

            int done = 0;
            long freed = 0;
            foreach (FileEntry f in sel)
            {
                if (File.Exists(f.FullPath)) continue;
                done++;
                freed += f.Size;
                DeleteLog.Write(f.FullPath, f.Size, "Duplicate moved to Recycle Bin");
                RemoveFileFromModel(f);
            }
            AfterDelete(done, freed, "duplicate file(s)");
        }

        // ================= filter =================
        void ApplyFilter()
        {
            filter = FilterSpec.Parse(search.Box.Text);
            if (root != null && scanner != null && !scanning)
            {
                FillTypes();
                FillFiles();
                if (current != null) NavigateTo(current);
            }
            FillDuplicates();
            if (!filter.IsEmpty && (filter.Exts.Count > 0 || filter.OlderThan != DateTime.MinValue || filter.NewerThan != DateTime.MinValue))
                lblStatus.Text = "Filter applied. File types and dates filter the file lists; folders are filtered by name and size.";
        }

        // ================= Cleanup Advisor view =================
        bool analyzedOnce;

        void BuildAdvisorView()
        {
            advisorView = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };

            var top = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Theme.Bg };
            var texts = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(2, 4, 0, 0) };
            lblAdvSub = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, AutoEllipsis = true };
            lblAdvHead = new Label { Dock = DockStyle.Top, Height = 30, Font = Theme.Big, ForeColor = Theme.Ink, AutoEllipsis = true };
            texts.Controls.Add(lblAdvSub);
            texts.Controls.Add(lblAdvHead);
            btnClean = new FlatBtn("Clean selected", BtnKind.Primary);
            btnClean.Width = 230;
            btnCheckAgain = new FlatBtn("Check again", BtnKind.Secondary);
            btnClean.Click += (s, e) => StartClean();
            btnCheckAgain.Click += (s, e) => StartAnalyze();
            top.Controls.Add(texts);
            top.Controls.Add(RightFlow(12, btnClean, btnCheckAgain));

            splitAdvisor = MakeSplit();
            advisorFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = Theme.Bg
            };
            AddAdvisorGroup("Safe to clean", Safety.Safe);
            AddAdvisorGroup("Check before cleaning", Safety.Review);
            AddAdvisorGroup("Leave these alone", Safety.LeaveAlone);
            advisorFlow.Resize += (s, e) => SizeAdvisorRows();
            splitAdvisor.Panel1.Controls.Add(advisorFlow);

            var cardDonut = new Card { Title = "Selected to clean" };
            advisorDonut = new Donut { Dock = DockStyle.Fill };
            advisorDonut.EmptyText = "Tick items on the left to see how much space you will get back.";
            cardDonut.Controls.Add(advisorDonut);
            splitAdvisor.Panel2.Controls.Add(cardDonut);

            advisorView.Controls.Add(splitAdvisor);
            advisorView.Controls.Add(top);
            RefreshAdvisor();
        }

        void AddAdvisorGroup(string title, Safety level)
        {
            var lbl = new Label
            {
                Text = title, Font = Theme.BodyBold, ForeColor = Theme.Ink, AutoSize = false, Height = 28,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(2, advisorFlow.Controls.Count == 0 ? 0 : 10, 0, 6)
            };
            advisorFlow.Controls.Add(lbl);
            foreach (CleanupRule r in rules.Where(x => x.Level == level))
            {
                var row = new AdvisorRow(r);
                row.Toggled += () => { RefreshAdvisor(); UpdateButtons(); };
                CleanupRule thisRule = r;
                row.ViewRequested += () => OpenRuleFiles(thisRule);
                if (r.Kind != RuleKind.Info)
                {
                    CleanupRule rule = r;
                    var m = new ContextMenuStrip();
                    if (r.IsFileList) m.Items.Add("Review and choose files", null, (s, e) => OpenRuleFiles(rule));
                    m.Items.Add("Open location", null, (s, e) => OpenRuleLocation(rule));
                    row.ContextMenuStrip = m;
                }
                advisorRows.Add(row);
                advisorFlow.Controls.Add(row);
            }
        }

        void SizeAdvisorRows()
        {
            int w = advisorFlow.Width - SystemInformation.VerticalScrollBarWidth - 8;
            if (w < 200) return;
            foreach (Control c in advisorFlow.Controls) c.Width = w;
        }

        void OpenRuleFiles(CleanupRule r)
        {
            if (!r.Analyzed || r.Hits.Count == 0)
            {
                if (advReadOnly && r.IsFileList && r.DisplayCount > 0)
                    MessageBox.Show(this, "This category has " + r.DisplayCount + " files on the VM, but only the first several thousand were brought back for review. You can still clean the whole category with Clean selected.", "Remote review", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (advBusy) return;
            DialogResult res;
            bool changed;
            using (var f = new RuleFilesForm(r))
            {
                res = f.ShowDialog(this);
                changed = f.Changed;
            }
            r.Recalc();
            if (changed) r.Checked = r.SelCount > 0;
            RefreshAdvisor();
            UpdateButtons();
            if (res == DialogResult.Yes) StartClean(new List<CleanupRule> { r });
        }

        void OpenRuleLocation(CleanupRule r)
        {
            if (r.Kind == RuleKind.ScanBackups || r.Kind == RuleKind.Hibernation)
            {
                if (r.Hits.Count > 0) Util.OpenInExplorer(r.Hits[0].Path, true);
                return;
            }
            if (!string.IsNullOrEmpty(r.OpenPath) && Directory.Exists(r.OpenPath))
            {
                Util.OpenInExplorer(r.OpenPath, false);
                return;
            }
            if (r.Targets != null)
            {
                foreach (string t in r.Targets())
                    if (Directory.Exists(t)) { Util.OpenInExplorer(t, false); return; }
            }
            if (r.Hits.Count > 0) Util.OpenInExplorer(r.Hits[0].Path, true);
            else lblStatus.Text = "That location does not exist on this VM.";
        }

        void RefreshAdvisor()
        {
            foreach (AdvisorRow row in advisorRows) row.Invalidate();

            long safe = rules.Where(r => r.Analyzed && r.Level == Safety.Safe && r.Kind != RuleKind.ComponentCleanup).Sum(r => r.Size);
            long review = rules.Where(r => r.Analyzed && r.Level == Safety.Review && r.Kind != RuleKind.ComponentCleanup).Sum(r => r.Size);
            List<CleanupRule> sel = rules.Where(r => r.Checked && r.Analyzed && r.HasSelection).ToList();
            long selSize = sel.Sum(r => r.SelectedBytes);

            if (advCleaning)
            {
                lblAdvHead.Text = "Cleaning up...";
                lblAdvSub.Text = "Files that are in use are skipped. You can keep using the other tabs.";
            }
            else if (advBusy || !analyzedOnce)
            {
                lblAdvHead.Text = "Checking " + CurrentMachineName() + " for files that are safe to remove...";
                lblAdvSub.Text = safe > 0 ? "Found " + Util.Size(safe) + " so far." : "This usually takes less than a minute.";
            }
            else if (safe > 0)
            {
                lblAdvHead.Text = "You can free about " + Util.Size(safe) + " safely on " + CurrentMachineName() + ".";
                lblAdvSub.Text = (review > 0 ? Util.Size(review) + " more is in items to check first. " : "") +
                                 "Tick what you want to remove, then click Clean selected.";
            }
            else
            {
                lblAdvHead.Text = "No safe cleanups found. This VM is already tidy.";
                lblAdvSub.Text = review > 0 ? Util.Size(review) + " is in the items to check first, below."
                                            : "Use Scan drives to find what else is using space.";
            }

            btnClean.Text = selSize > 0 ? "Clean selected (" + Util.Size(selSize) + ")" : "Clean selected";
            btnClean.Invalidate();

            List<Seg> segs = sel.Where(r => r.SelectedBytes > 0)
                .OrderByDescending(r => r.SelectedBytes)
                .Select(r => new Seg { Label = r.Title, Value = r.SelectedBytes, Color = r.Color }).ToList();
            advisorDonut.SetData(segs, Util.Size(selSize), "will be freed");
        }

        bool advQuiet;   // true when the check runs by itself after a scan

        void StartAnalyze() { StartAnalyze(false); }

        void StartAnalyze(bool quiet)
        {
            if (remoteMode || advReadOnly)
            {
                lblAdvHead.Text = "Cleanup advisor (read-only) for " + (remoteHost ?? "this VM");
                lblAdvSub.Text = "These are the remote VM's numbers. To clean, open the VM with Remote Desktop and run the app there.";
                return;
            }
            if (advBusy)
            {
                if (!advCleaning) reanalyzePending = true;
                return;
            }
            advBusy = true;
            List<FileEntry> scanFiles = (scanner != null && !scanning)
                ? new HashSet<FileEntry>(scanner.TopFiles.Concat(scanner.BigFiles)).ToList() : null;
            Advisor adv = new Advisor();
            adv.UseIndex = chkFast.Checked;
            advQuiet = quiet;
            advisor = adv;
            advStart = DateTime.Now;
            UpdateButtons();
            RefreshAdvisor();

            var t = new Thread(() =>
            {
                try { adv.Analyze(rules, scanFiles); } catch { }
                try { SafeInvoke(AnalyzeFinished); } catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            advTimer.Start();
        }

        DateTime advStart;

        void AnalyzeFinished()
        {
            advBusy = false;
            if (!scanning && !advCleaning && !advQuiet && advisor != null)
                lblStatus.Text = string.Format("Cleanup check finished in {0:0.0} seconds{1}.",
                    (DateTime.Now - advStart).TotalSeconds,
                    advisor.IndexedDrives > 0 ? " using the fast disk index" : "");
            analyzedOnce = true;
            advTimer.Stop();
            progressStrip.Invalidate();
            RefreshAdvisor();
            UpdateButtons();
            if (reanalyzePending)
            {
                reanalyzePending = false;
                StartAnalyze();
            }
        }

        void OnAdvisorTick()
        {
            if (!scanning)
            {
                progressPhase++;
                progressStrip.Invalidate();
            }
            if (advBusy && !scanning && advisor != null && (advCleaning || !advQuiet))
            {
                string st = advisor.Status;
                if (!string.IsNullOrEmpty(st)) lblStatus.Text = st;
            }
            RefreshAdvisor();
        }

        void StartClean() { StartClean(null); }

        void StartClean(List<CleanupRule> only)
        {
            if (advReadOnly)
            {
                // If specific files were ticked in a reviewed category, delete exactly those on the VM.
                var ruleList = only ?? rules.Where(r => r.Checked).ToList();
                var picked = ruleList.Where(r => r.Analyzed && r.Hits.Count > 0).SelectMany(r => r.Hits.Where(h => h.Selected).Select(h => h.Path)).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                var wholeCats = ruleList.Where(r => r.Analyzed && r.Hits.Count == 0 && r.Size > 0).ToList();
                if (picked.Count > 0 && wholeCats.Count == 0) { RemoteDelete(picked, "cleanup file(s)"); return; }
                RemoteCleanup();
                return;
            }
            if (advBusy) return;
            List<CleanupRule> todo = (only ?? rules.Where(r => r.Checked).ToList())
                .Where(r => r.Analyzed && r.HasSelection).ToList();
            if (todo.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one item that has something to clean.", "Cleanup Advisor",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.Append("These items will be permanently deleted. They will not go to the Recycle Bin.\n\n");
            foreach (CleanupRule r in todo)
                sb.Append("-  " + r.Title + ":  " +
                          (r.Kind == RuleKind.ComponentCleanup ? "size decided by Windows"
                           : r.IsFileList ? string.Format("{0} ({1:N0} files)", Util.Size(r.SelSize), r.SelCount)
                           : Util.Size(r.Size)) + "\n");
            sb.Append("\nFiles that are in use will be skipped.");
            if (todo.Any(r => r.Level == Safety.Review))
                sb.Append("\n\nYou selected items marked \"Check first\". Make sure nobody needs them.");
            if (todo.Any(r => r.Kind == RuleKind.Hibernation))
                sb.Append("\n\nHibernation will be turned off. Virtual machines almost never need it.");
            if (todo.Any(r => r.Kind == RuleKind.ComponentCleanup))
                sb.Append("\n\nWindows component cleanup can take 10 to 30 minutes. Keep the app open until it finishes.");
            sb.Append("\n\nStart cleaning?");
            if (MessageBox.Show(this, sb.ToString(), "Confirm cleanup", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            advBusy = true;
            advCleaning = true;
            Advisor adv = new Advisor();
            advisor = adv;
            UpdateButtons();
            RefreshAdvisor();

            var t = new Thread(() =>
            {
                try { adv.Clean(todo); } catch { }
                try { SafeInvoke((Action)(() => CleanFinished(todo))); } catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            advTimer.Start();
        }

        void CleanFinished(List<CleanupRule> todo)
        {
            advBusy = false;
            advCleaning = false;
            advTimer.Stop();
            progressStrip.Invalidate();

            long freed = todo.Sum(r => r.Freed);
            int skipped = todo.Sum(r => r.Skipped);
            foreach (CleanupRule r in todo)
            {
                if (r.Deleted > 0 || r.Freed > 0 || r.Kind == RuleKind.ComponentCleanup)
                    DeleteLog.Write("Cleanup Advisor: " + r.Title + " (" + r.Deleted.ToString("N0") + " files)", r.Freed, "Permanently deleted");
            }
            foreach (DriveCard c in driveCards) c.Reload();

            int deletedFiles = todo.Where(r => r.IsFileList).Sum(r => r.Deleted);
            double secs = todo.Sum(r => r.Seconds);
            string msg = "Cleanup finished. Freed " + Util.Size(freed) + ".";
            if (deletedFiles > 0)
                msg += string.Format(" Deleted {0:N0} files in {1:0.0} seconds.", deletedFiles, Math.Max(0.1, secs));
            lblStatus.Text = msg + (root != null ? " Click Scan drives again to refresh the folder and file lists." : "");
            if (skipped > 0)
                msg += "\n\n" + skipped.ToString("N0") + " file(s) were in use by running programs and were skipped. " +
                       "Most of them can be removed after the VM restarts.";
            UpdateButtons();
            MessageBox.Show(this, msg, "Cleanup Advisor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartAnalyze(true);
        }

        SplitContainer MakeSplit()
        {
            var sc = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 14,
                BackColor = Theme.Bg, FixedPanel = FixedPanel.None
            };
            sc.Panel1.BackColor = Theme.Bg;
            sc.Panel2.BackColor = Theme.Bg;
            return sc;
        }

        void AttachMenu(DataGridView grid, bool files)
        {
            var m = new ContextMenuStrip();
            m.Items.Add(files ? "Show in Explorer" : "Open in Explorer", null, (s, e) =>
            {
                if (files) ShowSelectedFile(); else OpenSelectedFolder();
            });
            m.Items.Add("Copy path", null, (s, e) => CopyPaths(grid));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Move to Recycle Bin", null, (s, e) =>
            {
                if (files) DeleteSelectedFiles(); else DeleteSelectedFolders();
            });
            grid.ContextMenuStrip = m;
            grid.CellMouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !grid.Rows[e.RowIndex].Selected)
                {
                    grid.ClearSelection();
                    grid.Rows[e.RowIndex].Selected = true;
                }
            };
        }

        // ================= painting helpers =================
        void PaintProgress(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = progressStrip.Width;
            if (!scanning && !advBusy && !dupBusy)
            {
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, 0, w, 0);
                return;
            }
            int seg = Math.Max(40, w / 4);
            int x = (progressPhase * 10) % (w + seg) - seg;
            using (var b = new SolidBrush(Theme.AccentSoft)) g.FillRectangle(b, 0, 0, w, 3);
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, x, 0, seg, 3);
        }

        Color? RowColor(DataGridView grid, int row)
        {
            object tag = grid.Rows[row].Tag;
            if (grid == gridDups)
            {
                FileEntry df = tag as FileEntry;
                Color dc;
                if (df != null && dupColors.TryGetValue(df, out dc)) return dc;
                return null;
            }
            RowItem ri = tag as RowItem;
            if (ri != null) return ri.Color;
            FileEntry fe = tag as FileEntry;
            if (fe != null) return ExtColor(fe.Ext);
            if (tag is Color) return (Color)tag;
            return null;
        }

        Color ExtColor(string ext)
        {
            Color c;
            return extColors.TryGetValue(ext ?? "", out c) ? c : Theme.Gray;
        }

        void PaintCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            var grid = (DataGridView)sender;
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "share") { GridStyle.PaintShare(e); return; }
            if (col != "name") return;
            Color? dot = RowColor(grid, e.RowIndex);
            if (dot == null) return;

            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.CellBounds;
            using (var b = new SolidBrush(dot.Value)) g.FillEllipse(b, r.X + 10, r.Y + r.Height / 2f - 5, 10, 10);
            TextRenderer.DrawText(g, Convert.ToString(e.Value), Theme.Body,
                new Rectangle(r.X + 28, r.Y, r.Width - 32, r.Height), Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            e.Handled = true;
        }

        // ================= drives & views =================
        List<VmDriveTile> vmTiles = new List<VmDriveTile>();

        void LoadDrives()
        {
            foreach (DriveCard c in driveCards) c.Dispose();
            foreach (VmDriveTile v in vmTiles) v.Dispose();
            drivesFlow.Controls.Clear();
            driveCards.Clear();
            vmTiles.Clear();

            if (active != null && active.IsRemote)
            {
                // VM drives (from the scan). Ticking one and clicking "Scan drives" re-scans that VM.
                foreach (string letter in active.DriveLetters)
                {
                    DriveMeta dm = active.Drives != null ? active.Drives.FirstOrDefault(d => d.Letter != null && d.Letter.TrimEnd('\\', ':').Equals(letter.TrimEnd('\\', ':'), StringComparison.OrdinalIgnoreCase)) : null;
                    var tile = new VmDriveTile(letter, dm != null ? dm.Total : 0, dm != null ? dm.Free : 0);
                    tile.Click += (s, e) => UpdateButtons();
                    vmTiles.Add(tile);
                    drivesFlow.Controls.Add(tile);
                }
                foreach (VmDriveTile t in vmTiles) t.Selected = true;
                UpdateButtons();
                return;
            }

            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try { if (!d.IsReady || d.DriveType != DriveType.Fixed) continue; }
                catch { continue; }
                var card = new DriveCard(d);
                card.Click += (s, e) => UpdateButtons();
                driveCards.Add(card);
                drivesFlow.Controls.Add(card);
            }
            if (driveCards.Count > 0) driveCards.OrderByDescending(c => c.UsedPct).First().Selected = true;
            UpdateButtons();
        }

        void ShowView(int i)
        {
            folderView.Visible = i == 0;
            filesView.Visible = i == 1;
            typesView.Visible = i == 2;
            usersView.Visible = i == 5;
            if (btnUsers != null) btnUsers.Active = i == 5;
            advisorView.Visible = i == 3;
            dupView.Visible = i == 4;
            btnDups.Active = i == 4;
            if (i == 3) SizeAdvisorRows();
            btnAdvisor.Active = i == 3;
            btnFolders.Active = i == 0;
            btnFiles.Active = i == 1;
            btnTypes.Active = i == 2;

            SplitContainer sc = i == 0 ? splitFolders : i == 1 ? splitFiles : i == 2 ? splitTypes : i == 3 ? splitAdvisor : i == 5 ? splitUsers : splitDups;
            if (sc.Tag == null && sc.Width > 300)
            {
                double f = i == 0 ? 0.47 : i == 1 ? 0.62 : i == 2 ? 0.5 : i == 3 ? 0.64 : i == 5 ? 0.5 : 0.62;
                try { sc.SplitterDistance = (int)(sc.Width * f); sc.Tag = "ready"; } catch { }
            }
        }

        void UpdateButtons()
        {
            bool anySelected = driveCards.Any(c => c.Selected) || vmTiles.Any(t => t.Selected);
            bool haveData = root != null && !scanning && !advCleaning;
            btnScan.Enabled = !scanning && !advCleaning && !dupBusy && !remoteBusy && anySelected;
            chkFast.Enabled = !scanning;
            btnStop.Enabled = scanning;
            btnRefresh.Enabled = !scanning;
            btnEmptyBin.Enabled = !scanning;
            if (btnSemPlants != null)
                btnSemPlants.Enabled = haveData && active != null && active.IniScanFinished;
            foreach (DriveCard c in driveCards) c.Enabled = !scanning;
            btnUp.Enabled = haveData && current != null && current.Parent != null;
            btnOpenFolder.Enabled = haveData && !remoteMode;
            btnDeleteFolder.Enabled = haveData && !remoteBusy;
            btnShowFile.Enabled = haveData && !remoteMode;
            btnDeleteFiles.Enabled = haveData && !remoteBusy;
            btnExport.Enabled = haveData;
            if (btnFindDups != null)
            {
                btnFindDups.Enabled = dupBusy || (root != null && !scanning && !advCleaning);
                btnFindDups.Text = dupBusy ? "Stop checking" : "Find duplicates";
                btnFindDups.Invalidate();
                bool haveDups = dupGroups != null && dupGroups.Count > 0 && !dupBusy && !scanning && !advCleaning;
                btnDupSelect.Enabled = haveDups;
                btnDupDelete.Enabled = haveDups;
                btnDupShow.Enabled = haveDups;
            }
            if (btnClean != null)
            {
                btnCheckAgain.Enabled = !advBusy;
                btnClean.Enabled = !advBusy && rules.Any(r => r.Checked && r.Analyzed && r.HasSelection);
                foreach (AdvisorRow row in advisorRows) row.Enabled = !advCleaning;
            }
        }

        // ================= scanning =================
        void StartScan()
        {
            if (active != null && active.IsRemote)
            {
                var letters = vmTiles.Where(t => t.Selected).Select(t => t.Letter).ToList();
                if (letters.Count == 0) { MessageBox.Show(this, "Tick at least one of this VM's drives first.", "Space Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                active.DriveLetters = letters;
                ScanActiveRemote();
                return;
            }
            List<string> roots = driveCards.Where(c => c.Selected).Select(c => c.Drive.RootDirectory.FullName).ToList();
            if (roots.Count == 0)
            {
                MessageBox.Show(this, "Select at least one drive first.", "Space Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            remoteHost = null; remoteMode = false; advReadOnly = false; remoteAdv = null;
            StartScanCore(roots, chkFast.Checked);
        }

        // Called by the Network VMs window to scan another computer over the network.
        public void ScanRemote(string host, List<string> uncRoots)
        {
            if (scanning) return;
            remoteHost = host;
            foreach (DriveCard c in driveCards) c.Selected = false;   // these are the local drives; not what we're scanning
            StartScanCore(uncRoots, false);   // the fast disk index can't be used over the network
            lblStatus.Text = "Scanning " + host + " over the network. This uses the normal method and can take a while...";
        }

        // Deploy-and-run: copy the app to the VM, scan there, show results here.
        // Queue one or more VMs to scan. The first starts now; the rest run automatically.
        public void QueueRemoteScans(List<string[]> vms)
        {
            // each item: [0]=host, [1..]=drive letters ; uncRoots derived as \\host\C$
            SaveActive();
            MachineSession first = null;
            foreach (string[] vm in vms)
            {
                string host = vm[0];
                var letters = vm.Skip(1).ToList();
                MachineSession m = GetOrAddRemote(host);
                m.DriveLetters = letters;
                m.UncRoots = letters.Select(l => "\\\\" + host + "\\" + l.TrimEnd(':') + "$").ToList();
                m.HasResult = false;
                if (first == null) first = m; else scanQueue.Enqueue(m);
            }
            RebuildMachineTabs();
            if (first != null) { active = first; RebuildMachineTabs(); ScanActiveRemote(); }
        }

        public void ScanRemoteAgent(string host, List<string> driveLetters, List<string> uncRootsForFallback)
        {
            if (scanning) return;
            MachineSession m = GetOrAddRemote(host);
            m.DriveLetters = driveLetters;
            m.UncRoots = uncRootsForFallback;
            active = m;
            scanning = true;
            remoteHost = host; remoteMode = true;
            scanStart = DateTime.Now;
            LoadDrives();
            RebuildMachineTabs();
            UpdateButtons();
            progressStrip.Invalidate();
            uiTimer.Start();
            lblStatus.Text = "Connecting to " + host + "...";

            var t = new Thread(() =>
            {
                RemoteResult res = null; Exception err = null;
                try { res = RemoteAgent.Run(host, driveLetters, msg => SetStatus(msg), () => false); }
                catch (Exception ex) { err = ex; }
                Action done = () => { try { RemoteFinished(res, err, host, uncRootsForFallback); } catch (Exception ex2) { scanning = false; remoteMode = false; remoteHost = null; uiTimer.Stop(); lblStatus.Text = "Could not show results: " + ex2.Message; UpdateButtons(); } };
                SafeInvoke(done);
            }, 32 * 1024 * 1024);
            t.IsBackground = true; t.Start();
        }

        void SetStatus(string m) { try { if (IsHandleCreated) SafeInvoke((Action)(() => { remoteStatus = m; lblStatus.Text = m; })); } catch { } }

        void RemoteFinished(RemoteResult res, Exception err, string host, List<string> uncRootsForFallback)
        {
            uiTimer.Stop();
            scanning = false;
            progressStrip.Invalidate();
            if (err != null || res == null || res.Root == null)
            {
                string msg = err != null ? err.Message : "No results came back.";
                if (!(err is OperationCanceledException) &&
                    MessageBox.Show(this, "Deploy-and-run could not finish on " + host + ":\n\n" + msg +
                        "\n\nScan it the slower file-by-file way over the network instead?",
                        "Deploy-and-run failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    ScanRemote(host, uncRootsForFallback);
                    return;
                }
                remoteMode = false; remoteHost = null;
                lblStatus.Text = "Deploy-and-run stopped: " + msg;
                UpdateButtons();
                return;
            }

            scanner = new Scanner();
            scanner.TopFiles = res.TopFiles;
            scanner.Exts = res.Exts;
            scanner.BigFiles = new List<FileEntry>();
            scanner.IniPaths = new List<string>(res.IniPaths);
            active.IniPaths = new List<string>(res.IniPaths);
            active.IniScanFinished = true;
            scanner.Files = res.Files; scanner.Bytes = res.Bytes;
            root = res.Root;
            dupGroups = null;
            remoteAdv = res.Adv;

            FillTypes();
            FillFiles();
            NavigateTo(root);
            FillDuplicates();
            ApplyRemoteAdvisor();
            UpdateSummary();
            UpdateButtons();
            ShowView(0);
            lblStatus.Text = string.Format("Scanned {0} on the VM: {1:N0} files using {2}, in {3:0} seconds (fast).",
                host, res.Files, Util.Size(res.Bytes), res.Seconds);
            active.Seconds = res.Seconds;
            active.Drives = res.Drives;
            StoreActiveResult();
            RebuildMachineTabs();
            RunNextQueued();
        }

        bool remoteBusy;

        string CurrentMachineName()
        {
            if (active != null) return active.IsRemote ? active.Name : Environment.MachineName;
            return remoteMode && remoteHost != null ? remoteHost : Environment.MachineName;
        }

        void ResetAdvisorRules()
        {
            foreach (CleanupRule r in rules)
            {
                r.Size = 0; r.DisplayCount = -1; r.Hits = new List<FileHit>();
                r.SelSize = 0; r.SelCount = 0; r.Analyzed = false;
                r.Checked = r.Level == Safety.Safe;
            }
        }

        void ApplyRemoteAdvisor()
        {
            advReadOnly = true;
            if (remoteAdv == null) return;
            var byTitle = new Dictionary<string, AdvRow>(StringComparer.OrdinalIgnoreCase);
            foreach (AdvRow a in remoteAdv) byTitle[a.Title] = a;
            foreach (CleanupRule r in rules)
            {
                AdvRow a;
                if (byTitle.TryGetValue(r.Title, out a))
                {
                    r.Size = a.Size;
                    r.DisplayCount = (int)a.Count;
                    r.Hits = a.Files != null ? a.Files : new List<FileHit>();
                    foreach (FileHit h in r.Hits) h.Selected = true;
                    r.Recalc();
                    r.Analyzed = true;
                    r.Checked = false;
                }
                else { r.Size = 0; r.DisplayCount = 0; r.Hits = new List<FileHit>(); r.Analyzed = true; r.Checked = false; }
            }
            RefreshAdvisor();
        }

        void RemoteDelete(List<string> paths, string what)
        {
            if (remoteBusy) return;
            paths = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            if (paths.Count == 0) { MessageBox.Show(this, "Select something first.", "Delete on VM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string msg = string.Format("Permanently delete {0} {1} on {2}?\n\n{3}{4}\n\nThere is no Recycle Bin on a remote VM, so this cannot be undone.",
                paths.Count, what, remoteHost, string.Join("\n", paths.Take(8)), paths.Count > 8 ? "\n...and " + (paths.Count - 8) + " more" : "");
            if (MessageBox.Show(this, msg, "Delete on " + remoteHost, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            RunRemoteJob("/delete", paths, "Deleting " + paths.Count + " item(s) on " + remoteHost + "...");
        }

        void RemoteCleanup()
        {
            if (remoteBusy) return;
            List<string> titles = rules.Where(r => r.Checked && r.Analyzed && r.Size > 0).Select(r => r.Title).ToList();
            if (titles.Count == 0) { MessageBox.Show(this, "Tick the cleanup items you want to run on the VM first.", "Clean on VM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            long est = rules.Where(r => titles.Contains(r.Title)).Sum(r => r.Size);
            if (MessageBox.Show(this, string.Format("Run {0} cleanup item(s) on {1}?\n\nAbout {2} will be freed. These are deleted permanently on the VM.", titles.Count, remoteHost, Util.Size(est)),
                    "Clean on " + remoteHost, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            RunRemoteJob("/cleanup", titles, "Cleaning on " + remoteHost + "...");
        }

        void RunRemoteJob(string mode, List<string> lines, string startMsg)
        {
            remoteBusy = true;
            advBusy = mode == "/cleanup";
            UpdateButtons();
            lblStatus.Text = startMsg;
            string host = remoteHost;
            var t = new Thread(() =>
            {
                RemoteAgent.JobResult r = null; Exception err = null;
                try { r = RemoteAgent.RunJob(host, mode, lines, m => SetStatus(m), () => false); }
                catch (Exception ex) { err = ex; }
                try { SafeInvoke((Action)(() => RemoteJobDone(r, err, host))); } catch { }
            });
            t.IsBackground = true; t.Start();
        }

        void RemoteJobDone(RemoteAgent.JobResult r, Exception err, string host)
        {
            remoteBusy = false; advBusy = false;
            if (err != null || r == null)
            {
                lblStatus.Text = "The job failed: " + (err != null ? err.Message : "unknown");
                MessageBox.Show(this, "Could not finish on " + host + ":\n\n" + (err != null ? err.Message : "unknown error"), "Remote job", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateButtons();
                return;
            }
            lblStatus.Text = string.Format("Done on {0}: freed {1}, deleted {2:N0} item(s){3}. Click Scan selected VM again to refresh.",
                host, Util.Size(r.Freed), r.Deleted, r.Skipped > 0 ? ", skipped " + r.Skipped : "");
            MessageBox.Show(this, string.Format("Freed {0} on {1}.\nDeleted {2:N0} item(s){3}.\n\nRe-scan the VM to see the updated space.",
                Util.Size(r.Freed), host, r.Deleted, r.Skipped > 0 ? ", skipped " + r.Skipped + " (in use or protected)" : ""),
                "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateButtons();
        }

        void StartScanCore(List<string> roots, bool fast)
        {
            active = LocalSession();
            active.IniScanFinished = false;
            active.IniPaths.Clear();
            remoteMode = false; remoteHost = null; advReadOnly = false; remoteAdv = null;
            ResetAdvisorRules(); analyzedOnce = false;
            RebuildMachineTabs();
            scanner = new Scanner();
            scanner.UseFast = fast;
            Scanner sc = scanner;
            dupGroups = null;
            scannedDrives = roots;
            scanning = true;
            scanStart = DateTime.Now;
            UpdateButtons();

            var t = new Thread(() =>
            {
                DirNode result = null;
                Exception err = null;
                try { result = sc.Run(roots); }
                catch (Exception ex) { err = ex; }
                try { SafeInvoke((Action)(() => ScanFinished(sc, result, err))); }
                catch { }
            }, 64 * 1024 * 1024);
            t.IsBackground = true;
            t.Start();
            uiTimer.Start();
        }

        void OnScanTick()
        {
            if (scanner == null) return;
            progressPhase++;
            progressStrip.Invalidate();
            if (scanner.Cancel) return;
            lblStatus.Text = string.Format("Scanning... {0:N0} files, {1} so far.   Reading {2}",
                Interlocked.Read(ref scanner.Files), Util.Size(Interlocked.Read(ref scanner.Bytes)), scanner.Current);
        }

        void ScanFinished(Scanner sc, DirNode result, Exception err)
        {
            uiTimer.Stop();
            scanning = false;
            progressStrip.Invalidate();
            if (err != null || result == null)
            {
                lblStatus.Text = "The scan could not finish: " + (err != null ? err.Message : "unknown error");
                UpdateButtons();
                return;
            }

            root = result;
            double secs = (DateTime.Now - scanStart).TotalSeconds;
            string method = sc.FastDrives > 0 && sc.SlowDrives == 0 ? " Fast scan (disk index) was used."
                          : sc.FastDrives > 0 ? string.Format(" Fast scan was used on {0} drive(s), normal scan on {1}.", sc.FastDrives, sc.SlowDrives)
                          : (sc.UseFast ? " The normal scan was used because the fast scan isn't available for this drive." : "");
            lblStatus.Text = string.Format("{0} {1:N0} files using {2}, in {3:0} seconds.{4}{5}",
                sc.Cancel ? "Scan stopped early. Showing what was found:" : "Scan complete:",
                root.FileCount, Util.Size(root.Size), secs,
                sc.Denied > 0 ? string.Format(" {0:N0} folders were skipped because Windows did not allow access.", sc.Denied) : "",
                method);

            FillTypes();
            FillFiles();
            NavigateTo(root);
            FillDuplicates();
            UpdateSummary();
            UpdateButtons();
            ShowView(0);
            StartAnalyze(true);
            StoreActiveResult();
            if (active != null && !active.IsRemote) StartIniDiscovery(sc, active);
            RebuildMachineTabs();
            RunNextQueued();
        }

        void StartIniDiscovery(Scanner source, MachineSession session)
        {
            session.IniScanFinished = false;
            session.IniPaths = new List<string>(source.IniPaths);
            lblStatus.Text = "Searching this computer for SEM site files...";
            UpdateButtons();
            var t = new Thread(() =>
            {
                List<string> found = IniFinder.FindFixedDrives();
                SafeInvoke((Action)(() =>
                {
                    if (session.Scanner != source) return;
                    source.IniPaths = found;
                    session.IniPaths = new List<string>(found);
                    session.IniScanFinished = true;
                    if (session == active)
                        lblStatus.Text = found.Count == 0
                            ? "Scan complete. No smartplantv4.ini files were found on this computer."
                            : "Scan complete. Found " + found.Count + " SEM site file(s). Click SEM plants to review them.";
                    UpdateButtons();
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        void UpdateSummary()
        {
            if (root == null) return;
            string who = active != null ? active.Name : (remoteHost ?? "This computer");
            if (remoteMode)
                lblSummary.Text = string.Format("{0}  (network VM):  {1:N0} files using {2}", who, root.FileCount, Util.Size(root.Size));
            else
                lblSummary.Text = string.Format("This computer ({0}):  {1:N0} files using {2}", Environment.MachineName, root.FileCount, Util.Size(root.Size));
        }

        // ================= folder view =================
        void NavigateTo(DirNode node)
        {
            current = node;
            lblPath.Text = node.IsVirtual ? node.Name : node.FullPath;

            var rows = new List<RowItem>();
            foreach (DirNode c in node.Children)
                rows.Add(new RowItem { Name = c.Name, Size = c.Size, Count = c.FileCount, Node = c });
            if (node.OwnCount > 0)
                rows.Add(new RowItem { Name = "Files directly in this folder", Size = node.OwnSize, Count = node.OwnCount, IsFiles = true });
            rows.Sort((a, b) => b.Size.CompareTo(a.Size));

            int colorIdx = 0;
            foreach (RowItem r in rows) r.Color = r.IsFiles ? Theme.Gray : Theme.Pick(colorIdx++);
            if (!filter.IsEmpty)
            {
                rows = rows.Where(r => filter.MatchFolder(r.Name, r.Size) && !(r.IsFiles && filter.Words.Count > 0)).ToList();
                lblPath.Text += "   (filtered)";
            }

            double total = Math.Max(1, node.Size);
            gridFolders.SuspendLayout();
            gridFolders.Rows.Clear();
            foreach (RowItem r in rows.Take(2000))
            {
                int idx = gridFolders.Rows.Add(r.Name, Util.Size(r.Size), r.Size * 100.0 / total, r.Count.ToString("N0"));
                gridFolders.Rows[idx].Tag = r;
            }
            gridFolders.ResumeLayout();
            gridFolders.ClearSelection();

            var map = new List<MapItem>();
            long other = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                RowItem r = rows[i];
                if (i < 40)
                    map.Add(new MapItem
                    {
                        Label = r.Name, Size = r.Size, Tag = r, Color = r.Color,
                        Clickable = r.Node != null && (r.Node.Children.Count > 0 || r.Node.OwnCount > 0)
                    });
                else other += r.Size;
            }
            if (other > 0) map.Add(new MapItem { Label = "Other smaller items", Size = other, Color = Theme.Faint });
            treemap.EmptyText = rows.Count == 0 ? "This folder is empty." : "Choose a drive above and click Scan drives.";
            treemap.SetItems(map);
            UpdateButtons();
        }

        void OpenRow(RowItem r)
        {
            if (r == null || r.Node == null) return;
            if (r.Node.Children.Count == 0 && r.Node.OwnCount == 0) return;
            NavigateTo(r.Node);
        }

        // Double-clicking "Files directly in this folder": show those files so you can review/delete.
        void ShowFilesInCurrentFolder()
        {
            if (current == null || current.IsVirtual || string.IsNullOrEmpty(current.FullPath))
            {
                MessageBox.Show(this, "Open a specific folder first.", "Files", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!remoteMode)
            {
                // Local: list the files right here and show them in the review window.
                var local = new List<FileHit>();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(current.FullPath))
                    {
                        long sz = 0; DateTime mod = DateTime.MinValue;
                        try { var fi = new FileInfo(f); sz = fi.Length; mod = fi.LastWriteTime; } catch { }
                        local.Add(new FileHit { Path = f, Size = sz, Modified = mod, Selected = false });
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not read the folder: " + ex.Message, "Files", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                ShowFolderFiles(current.FullPath, local, false);
                return;
            }

            // Remote: ask the VM for just this folder's files.
            string host = remoteHost, folder = current.FullPath;
            lblStatus.Text = "Getting files in " + folder + " from " + host + "...";
            Enabled = false;
            var t = new Thread(() =>
            {
                List<FileHit> files = null; Exception err = null;
                try { files = RemoteAgent.ListFolder(host, folder, m => SetStatus(m), () => false); }
                catch (Exception ex) { err = ex; }
                try { SafeInvoke((Action)(() =>
                {
                    Enabled = true;
                    if (err != null) { lblStatus.Text = "Could not list the folder: " + err.Message; MessageBox.Show(this, err.Message, "Files", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    lblStatus.Text = string.Format("{0:N0} file(s) in {1}.", files.Count, folder);
                    ShowFolderFiles(folder, files, true);
                })); } catch { }
            });
            t.IsBackground = true; t.Start();
        }

        void ShowFolderFiles(string folder, List<FileHit> files, bool remote)
        {
            if (files.Count == 0) { MessageBox.Show(this, "No files directly in this folder.", "Files", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            // Reuse the advisor file-review window via a lightweight rule wrapper.
            var r = new CleanupRule { Title = "Files in " + folder, Level = Safety.Review, Kind = RuleKind.Files, Hits = files };
            r.Recalc(); r.Analyzed = true;
            DialogResult res;
            using (var f = new RuleFilesForm(r)) { res = f.ShowDialog(this); }
            var picked = r.Hits.Where(h => h.Selected).Select(h => h.Path).ToList();
            if (res == DialogResult.Yes && picked.Count > 0)
            {
                if (remote) RemoteDelete(picked, "file(s)");
                else LocalDeletePaths(picked);
            }
        }

        void LocalDeletePaths(List<string> paths)
        {
            paths = paths.Where(p => !Util.IsProtected(p) && File.Exists(p)).ToList();
            if (paths.Count == 0) { MessageBox.Show(this, "Nothing to delete (files may be protected or already gone).", "Delete", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (MessageBox.Show(this, string.Format("Move {0} file(s) to the Recycle Bin?", paths.Count), "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            Recycler.MoveToRecycleBin(this, paths);
            Cursor = Cursors.Default;
            int done = 0;
            foreach (string p in paths) { if (!File.Exists(p)) { done++; } }
            lblStatus.Text = string.Format("Sent {0} file(s) to the Recycle Bin.", done);
            if (!string.IsNullOrEmpty(remoteHost)) return;
            // refresh local scan view lightly
            foreach (DriveCard c in driveCards) c.Reload();
        }

        void GoUp()
        {
            if (current != null && current.Parent != null) NavigateTo(current.Parent);
        }

        List<RowItem> SelectedFolderRows()
        {
            return gridFolders.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as RowItem).Where(r => r != null && r.Node != null).ToList();
        }

        void OpenSelectedFolder()
        {
            List<RowItem> sel = SelectedFolderRows();
            if (sel.Count > 0) Util.OpenInExplorer(sel[0].Node.FullPath, false);
            else if (current != null && !current.IsVirtual) Util.OpenInExplorer(current.FullPath, false);
        }

        void DeleteSelectedFolders()
        {
            if (root == null || scanning || advCleaning) return;
            if (remoteMode)
            {
                var paths = SelectedFolderRows().Select(r => r.Node).Where(n => n != null && !n.IsVirtual && !string.IsNullOrEmpty(n.FullPath)).Select(n => n.FullPath).ToList();
                RemoteDelete(paths, "folder(s)");
                return;
            }
            List<RowItem> sel = SelectedFolderRows();
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "Select one or more folders in the list first.", "Space Analyzer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<DirNode> blocked = sel.Select(r => r.Node).Where(n => n.IsVirtual || n.Parent == null || Util.IsProtected(n.FullPath)).ToList();
            List<DirNode> ok = sel.Select(r => r.Node).Except(blocked).ToList();
            if (ok.Count == 0)
            {
                MessageBox.Show(this, "These are Windows system folders or drives. Space Analyzer won't delete them, to keep the VM working.",
                    "Protected folders", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            long total = ok.Sum(n => n.Size);
            string msg = string.Format("Move {0} folder(s) and everything inside them ({1}) to the Recycle Bin?\n\n{2}{3}",
                ok.Count, Util.Size(total), string.Join("\n", ok.Take(8).Select(n => n.FullPath)),
                ok.Count > 8 ? "\n...and " + (ok.Count - 8) + " more" : "");
            if (blocked.Count > 0) msg += "\n\n" + blocked.Count + " protected Windows folder(s) will be skipped.";
            msg += "\n\nIf a folder is too big for the Recycle Bin, Windows will ask before deleting it permanently.";
            if (MessageBox.Show(this, msg, "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            Recycler.MoveToRecycleBin(this, ok.Select(n => n.FullPath).ToList());
            Cursor = Cursors.Default;

            int done = 0;
            long freed = 0;
            foreach (DirNode n in ok)
            {
                if (Directory.Exists(n.FullPath)) continue;
                done++;
                freed += n.Size;
                DeleteLog.Write(n.FullPath, n.Size, "Folder moved to Recycle Bin");
                RemoveFolderFromModel(n);
            }
            AfterDelete(done, freed, "folder(s)");
        }

        void RemoveFolderFromModel(DirNode node)
        {
            DirNode p = node.Parent;
            if (p != null) p.Children.Remove(node);
            for (DirNode n = p; n != null; n = n.Parent)
            {
                n.Size -= node.Size;
                n.FileCount -= node.FileCount;
            }
            string prefix = node.FullPath.TrimEnd('\\') + "\\";
            scanner.TopFiles.RemoveAll(f => f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            scanner.BigFiles.RemoveAll(f => f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (dupGroups != null)
            {
                foreach (DupGroup g in dupGroups)
                    g.Files.RemoveAll(f => f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                dupGroups.RemoveAll(g => g.Files.Count < 2);
            }
        }

        // ================= files view =================
        void FillFiles()
        {
            List<FileEntry> list;
            if (filter.IsEmpty) list = scanner.TopFiles.OrderByDescending(f => f.Size).ToList();
            else
            {
                // when filtering, search the 500 largest files plus every file over 10 MB
                var pool = new HashSet<FileEntry>(scanner.TopFiles);
                pool.UnionWith(scanner.BigFiles);
                list = pool.Where(filter.MatchFile).OrderByDescending(f => f.Size).Take(2000).ToList();
            }
            gridFiles.SuspendLayout();
            gridFiles.Rows.Clear();
            foreach (FileEntry f in list)
            {
                int i = gridFiles.Rows.Add(f.Name, Util.Size(f.Size), f.Ext,
                    f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("yyyy-MM-dd HH:mm"), f.Folder.FullPath);
                gridFiles.Rows[i].Tag = f;
            }
            gridFiles.ResumeLayout();
            gridFiles.ClearSelection();

            if (!filter.IsEmpty)
                filesInfoBase = list.Count == 0
                    ? "No files match your filter. The filter searches the 500 largest files and every file over 10 MB."
                    : string.Format("{0:N0} files match your filter, {1} in total.", list.Count, Util.Size(list.Sum(f => f.Size)));
            else
                filesInfoBase = list.Count == 0 ? "No files found."
                    : string.Format("The {0:N0} largest files add up to {1}.", list.Count, Util.Size(list.Sum(f => f.Size)));
            UpdateFilesInfo();

            bars.SetData(list.Take(15).Select(f => new Seg
            {
                Label = f.Name, Detail = f.FullPath, Value = f.Size, Color = ExtColor(f.Ext)
            }).ToList());
        }

        List<FileEntry> SelectedFiles()
        {
            return gridFiles.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as FileEntry).Where(f => f != null).ToList();
        }

        void UpdateFilesInfo()
        {
            List<FileEntry> sel = SelectedFiles();
            lblFilesInfo.Text = sel.Count == 0 ? filesInfoBase
                : string.Format("{0}   Selected: {1:N0} file(s), {2}.", filesInfoBase, sel.Count, Util.Size(sel.Sum(f => f.Size)));
        }

        void ShowSelectedFile()
        {
            List<FileEntry> sel = SelectedFiles();
            if (sel.Count > 0) Util.OpenInExplorer(sel[0].FullPath, true);
        }

        void DeleteSelectedFiles()
        {
            if (root == null || scanning || advCleaning) return;
            if (remoteMode)
            {
                RemoteDelete(SelectedFiles().Select(f => f.FullPath).ToList(), "file(s)");
                return;
            }
            List<FileEntry> sel = SelectedFiles();
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "Select one or more files in the list first.", "Space Analyzer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<FileEntry> blocked = sel.Where(f => Util.IsProtected(f.FullPath)).ToList();
            List<FileEntry> ok = sel.Except(blocked).ToList();
            if (ok.Count == 0)
            {
                MessageBox.Show(this, "These are Windows system files. Space Analyzer won't delete them, to keep the VM working.",
                    "Protected files", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string msg = string.Format("Move {0} file(s), {1} in total, to the Recycle Bin?\n\n{2}{3}",
                ok.Count, Util.Size(ok.Sum(f => f.Size)), string.Join("\n", ok.Take(8).Select(f => f.FullPath)),
                ok.Count > 8 ? "\n...and " + (ok.Count - 8) + " more" : "");
            if (blocked.Count > 0) msg += "\n\n" + blocked.Count + " protected Windows file(s) will be skipped.";
            msg += "\n\nYou can restore them from the Recycle Bin until it is emptied.";
            if (MessageBox.Show(this, msg, "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            Recycler.MoveToRecycleBin(this, ok.Select(f => f.FullPath).ToList());
            Cursor = Cursors.Default;

            int done = 0;
            long freed = 0;
            foreach (FileEntry f in ok)
            {
                if (File.Exists(f.FullPath)) continue;
                done++;
                freed += f.Size;
                DeleteLog.Write(f.FullPath, f.Size, "File moved to Recycle Bin");
                RemoveFileFromModel(f);
            }
            AfterDelete(done, freed, "file(s)");
        }

        void RemoveFileFromModel(FileEntry f)
        {
            scanner.TopFiles.Remove(f);
            scanner.BigFiles.Remove(f);
            if (dupGroups != null)
            {
                foreach (DupGroup g in dupGroups) g.Files.Remove(f);
                dupGroups.RemoveAll(g => g.Files.Count < 2);
            }
            f.Folder.OwnSize -= f.Size;
            f.Folder.OwnCount--;
            for (DirNode n = f.Folder; n != null; n = n.Parent)
            {
                n.Size -= f.Size;
                n.FileCount--;
            }
            ExtStat es;
            if (scanner.Exts.TryGetValue(f.Ext, out es))
            {
                es.Size -= f.Size;
                es.Count--;
                if (es.Count <= 0) scanner.Exts.Remove(f.Ext);
            }
        }

        void AfterDelete(int done, long freed, string what)
        {
            FillTypes();
            FillFiles();
            if (current != null) NavigateTo(current);
            FillDuplicates();
            UpdateSummary();
            UpdateButtons();
            foreach (DriveCard c in driveCards) c.Reload();
            lblStatus.Text = done == 0
                ? "Nothing was deleted. The items may be in use by another program."
                : string.Format("Moved {0} {1} ({2}) to the Recycle Bin. Click Empty Recycle Bin to actually free the space.",
                    done, what, Util.Size(freed));
        }

        // ================= file types view =================
        void FillTypes()
        {
            List<ExtStat> list = scanner.Exts.Values.Where(x => x.Size > 0).OrderByDescending(x => x.Size).ToList();
            extColors.Clear();
            for (int i = 0; i < list.Count; i++) extColors[list[i].Ext] = i < 10 ? Theme.Pick(i) : Theme.Gray;

            long total = Math.Max(1, list.Sum(x => x.Size));
            gridTypes.SuspendLayout();
            gridTypes.Rows.Clear();
            foreach (ExtStat x in list.Where(t => filter.IsEmpty || filter.MatchType(t)).Take(1000))
            {
                int i = gridTypes.Rows.Add(x.Ext, Util.Size(x.Size), x.Size * 100.0 / total, x.Count.ToString("N0"));
                gridTypes.Rows[i].Tag = ExtColor(x.Ext);
            }
            gridTypes.ResumeLayout();
            gridTypes.ClearSelection();

            var segs = new List<Seg>();
            long other = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (i < 9) segs.Add(new Seg { Label = list[i].Ext, Value = list[i].Size, Color = ExtColor(list[i].Ext) });
                else other += list[i].Size;
            }
            if (other > 0) segs.Add(new Seg { Label = "All other types", Value = other, Color = Theme.Gray });
            donut.SetData(segs, Util.Size(list.Sum(x => x.Size)), list.Count.ToString("N0") + " file types");
        }

        // ================= other actions =================
        void CopyPaths(DataGridView grid)
        {
            var paths = new List<string>();
            foreach (DataGridViewRow r in grid.SelectedRows)
            {
                RowItem ri = r.Tag as RowItem;
                FileEntry fe = r.Tag as FileEntry;
                if (ri != null && ri.Node != null) paths.Add(ri.Node.FullPath);
                else if (fe != null) paths.Add(fe.FullPath);
            }
            if (paths.Count > 0)
            {
                try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); lblStatus.Text = "Path copied."; }
                catch { }
            }
        }

        void EmptyBin()
        {
            string vm = (active != null && active.IsRemote) ? active.Name : null;
            using (var f = new RecycleForm(vm)) f.ShowDialog(this);
            foreach (DriveCard c in driveCards) c.Reload();
        }

        void OpenLog()
        {
            if (!File.Exists(DeleteLog.FilePath))
            {
                MessageBox.Show(this, "Nothing has been deleted with Space Analyzer yet, so there is no log.",
                    "Deletion log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { Process.Start(DeleteLog.FilePath); }
            catch { Util.OpenInExplorer(DeleteLog.FilePath, true); }
        }

        void ExportReport()
        {
            if (root == null) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV file (opens in Excel) (*.csv)|*.csv";
                dlg.FileName = "SpaceReport_" + Environment.MachineName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var w = new StreamWriter(dlg.FileName, false, Encoding.UTF8))
                    {
                        w.WriteLine("Type,Path,Size,SizeBytes,Files,LastChanged");
                        foreach (DriveCard c in driveCards)
                            w.WriteLine(string.Join(",", Util.Csv("Drive"), Util.Csv(c.Drive.Name),
                                Util.Csv(Util.Size(c.Total - c.Free) + " used of " + Util.Size(c.Total)),
                                (c.Total - c.Free).ToString(CultureInfo.InvariantCulture), "", ""));
                        WriteFolders(w, root, 0, root.IsVirtual ? 2 : 1);
                        foreach (FileEntry f in scanner.TopFiles.OrderByDescending(x => x.Size))
                            w.WriteLine(string.Join(",", Util.Csv("File"), Util.Csv(f.FullPath), Util.Csv(Util.Size(f.Size)),
                                f.Size.ToString(CultureInfo.InvariantCulture), "1",
                                Util.Csv(f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("yyyy-MM-dd HH:mm"))));
                    }
                    lblStatus.Text = "Report saved to " + dlg.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save the report:\n" + ex.Message, "Export report",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        void WriteFolders(StreamWriter w, DirNode n, int depth, int maxDepth)
        {
            foreach (DirNode c in n.Children.OrderByDescending(x => x.Size).Take(depth == 0 ? 100 : 25))
            {
                w.WriteLine(string.Join(",", Util.Csv("Folder"), Util.Csv(c.FullPath), Util.Csv(Util.Size(c.Size)),
                    c.Size.ToString(CultureInfo.InvariantCulture), c.FileCount.ToString(CultureInfo.InvariantCulture), ""));
                if (depth + 1 < maxDepth) WriteFolders(w, c, depth + 1, maxDepth);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (scanner != null) scanner.Cancel = true;
            if (advisor != null && !advCleaning) advisor.Cancel = true;
            if (dupFinder != null) dupFinder.Cancel = true;
            base.OnFormClosing(e);
        }
    }
}
