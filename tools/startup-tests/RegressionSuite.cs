// Adapted from wpf-desktop's isolated regression suite; never accesses OS startup state.
using FileStash.Infrastructure.Shell;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Xml.Linq;
using System.Security.Principal;

namespace FileStash.StartupTests;

public static class RegressionSuite
{
    public static void Run()
    {
        var options = new StartupOptions("Example.Tests." + Guid.NewGuid().ToString("N"), "Example.exe");
        var target = ElevatedStartup.Target(options, @"D:\便携 & apps\Example.exe", "S-1-5-21-1-2-3-1001");
        string desired = ElevatedStartup.TaskXml(target);
        int passed = 0;
        void Check(bool condition, string message = "Assertion failed")
        { if (!condition) throw new Exception(message); }
        void Test(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { throw new Exception("FAIL " + name, ex); }
        }
        Exception Error(Action action)
        {
            try { action(); } catch (Exception ex) { return ex; }
            throw new Exception("Expected a failure");
        }
        string Changed(string xml, string name, string value)
        {
            var document = XDocument.Parse(xml);
            document.Descendants().Single(e => e.Name.LocalName == name).Value = value;
            return document.ToString();
        }

        void WithShortcutFiles(Action<string, MemoryStore> test)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var directory = Directory.CreateTempSubdirectory("wpf-shortcut-files-");
                    try { test(directory.FullName, new MemoryStore { ShortcutFiles = new(directory.FullName) }); }
                    finally { directory.Delete(true); }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Test("TaskScheduler task lookup preserves query failures and recognizes only missing task HRESULTs", () =>
        {
            foreach (Exception missing in new Exception[] { new FileNotFoundException(), new DirectoryNotFoundException(),
                new COMException("missing task", unchecked((int)0x80070002)), new COMException("missing task", unchecked((int)0x80070003)) })
                Check(WindowsStartupStore.ReadTaskXml(() => throw missing) is null);
            foreach (Exception failure in new Exception[] { new UnauthorizedAccessException(), new IOException("query failed"),
                new COMException("access denied", unchecked((int)0x80070005)), new COMException("service unavailable", unchecked((int)0x800706BA)) })
                Check(ReferenceEquals(Error(() => WindowsStartupStore.ReadTaskXml(() => throw failure)), failure));
            Check(WindowsStartupStore.ReadTaskXml(() => desired) == desired);
        });
        Test("Chinese, spaces and XML metacharacters survive task XML", () =>
        {
            var xml = XDocument.Parse(desired);
            Check(xml.Descendants().Single(e => e.Name.LocalName == "Command").Value == target.Executable);
            Check(ElevatedStartup.Matches(desired, target));
            Check(!desired.Contains("cmd /c", StringComparison.OrdinalIgnoreCase));
        });
        Test("the current apphost path works independently of bundle extraction", () =>
        {
            string processPath = Environment.ProcessPath!;
            if (Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                Error(() => ElevatedStartup.Target(options, processPath, target.UserSid));
                return;
            }
            var current = ElevatedStartup.Target(new(options.AppId, Path.GetFileName(processPath)),
                processPath, target.UserSid);
            Check(current.Executable == Environment.ProcessPath);
            Check(ElevatedStartup.Matches(ElevatedStartup.TaskXml(current), current));
        });
        Test("legacy outer quotes normalize; appended command arguments do not", () =>
        {
            Check(ElevatedStartup.Matches(Changed(desired, "Command", "\"" + target.Executable + "\""), target));
            Check(!ElevatedStartup.Matches(Changed(desired, "Command", "\"" + target.Executable + "\" --tray"), target));
            Check(!ElevatedStartup.Matches(Changed(desired, "Command", @"D:\Other\Example.exe"), target));
        });
        Test("relative paths and development hosts are rejected", () =>
        {
            Error(() => ElevatedStartup.NormalizePath("Example.exe"));
            Error(() => ElevatedStartup.Target(options, @"C:\dotnet\dotnet.exe", target.UserSid));
            Error(() => ElevatedStartup.Target(new("../other", "Example.exe"), target.Executable, target.UserSid));
        });
        Test("helper accepts only fixed startup operations for the original account", () =>
        {
            foreach (var operation in new[] { "install", "repair", "uninstall", "plain" })
                ElevatedStartup.ParseHelper([ElevatedStartup.HelperSwitch, operation, target.UserSid], target.UserSid);
            Error(() => ElevatedStartup.ParseHelper([ElevatedStartup.HelperSwitch, "install", "S-1-5-18"], target.UserSid));
            Error(() => ElevatedStartup.ParseHelper([ElevatedStartup.HelperSwitch, "run", target.UserSid], target.UserSid));
            Error(() => ElevatedStartup.ParseHelper([ElevatedStartup.HelperSwitch, "install", target.UserSid, "arbitrary.exe"], target.UserSid));
        });
        Test("task names isolate users", () =>
        {
            var other = ElevatedStartup.Target(options, target.Executable, "S-1-5-21-1-2-3-1002");
            Check(other.TaskName != target.TaskName);
            Check(!ElevatedStartup.Matches(ElevatedStartup.TaskXml(other), target));
        });
        Test("UAC cancellation changes no startup state", () =>
        {
            var store = new MemoryStore { Run = "previous", FailRead = true };
            var result = ElevatedStartup.Request(StartupOperation.Install, target, _ => throw new Win32Exception(1223), store);
            Check(result.Outcome == StartupOutcome.Cancelled && store.Writes == 0 && store.Run == "previous");
        });
        Test("the launch request fixes the exe, runas verb, operation and requester SID", () =>
        {
            var store = new MemoryStore { Task = desired };
            var result = ElevatedStartup.Request(StartupOperation.Install, target, info =>
            {
                Check(info.FileName == target.Executable && info.Verb == "runas" && info.UseShellExecute);
                Check(info.ArgumentList.SequenceEqual([ElevatedStartup.HelperSwitch, "install", target.UserSid, "3"]));
                return 0;
            }, store);
            Check(result.Outcome == StartupOutcome.Applied && store.Writes == 0);
        });
        Test("timeout is uncertainty and never replays the helper", () =>
        {
            int launches = 0;
            var store = new MemoryStore { Run = "previous", FailRead = true };
            var result = ElevatedStartup.Request(StartupOperation.Repair, target, _ => { launches++; return null; }, store);
            Check(result.Outcome == StartupOutcome.Uncertain && launches == 1 && store.Writes == 0);
        });
        Test("post-helper access denial is uncertainty, not a claimed rollback", () =>
        {
            var result = ElevatedStartup.Request(StartupOperation.Install, target, _ => 0, new MemoryStore { FailRead = true });
            Check(result.Outcome == StartupOutcome.Uncertain);
        });
        Test("helper account mismatch and registration failure are distinct", () =>
        {
            var store = new MemoryStore { FailRead = true };
            var mismatch = ElevatedStartup.Request(StartupOperation.Install, target, _ => 5, store);
            var failure = ElevatedStartup.Request(StartupOperation.Install, target, _ => 4, store);
            Check(mismatch.Outcome == StartupOutcome.Failed && mismatch.Detail!.Contains("account"));
            Check(failure.Outcome == StartupOutcome.Failed && failure.Detail!.Contains("exited 4"));
            Check(store.Writes == 0);
        });
        Test("privilege, logon, battery and lifetime changes fail readback", () =>
        {
            foreach (var (name, value) in new[] { ("RunLevel", "LeastPrivilege"), ("LogonType", "Password"),
                ("DisallowStartIfOnBatteries", "true"), ("StopIfGoingOnBatteries", "true"),
                ("ExecutionTimeLimit", "PT72H"), ("MultipleInstancesPolicy", "Parallel"), ("Arguments", "--help"),
                ("StartWhenAvailable", "false"),
                ("RunOnlyIfIdle", "true"), ("RunOnlyIfNetworkAvailable", "true") })
                Check(!ElevatedStartup.Matches(Changed(desired, name, value), target), name);
        });
        Test("registered XML may omit default enabled, network and idle flags", () =>
        {
            var xml = XDocument.Parse(desired);
            string[] defaults = ["Enabled", "RunOnlyIfNetworkAvailable", "RunOnlyIfIdle"];
            xml.Descendants().Where(e => defaults.Contains(e.Name.LocalName)).Remove();
            Check(ElevatedStartup.Matches(xml.ToString(), target));
            foreach (var (parent, field, value) in new[] {
                ("Settings", "Enabled", "false"), ("LogonTrigger", "Enabled", "false"),
                ("Settings", "RunOnlyIfNetworkAvailable", "true"), ("Settings", "RunOnlyIfIdle", "true"),
                ("Settings", "Enabled", ""), ("LogonTrigger", "Enabled", "invalid") })
            {
                var changed = XDocument.Parse(xml.ToString());
                var node = changed.Descendants().Single(e => e.Name.LocalName == parent);
                node.Add(new XElement(node.Name.Namespace + field, value));
                Check(!ElevatedStartup.Matches(changed.ToString(), target), parent + "/" + field + "=" + value);
            }
        });
        Test("registered account names and omitted defaults preserve SID isolation", () =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            var current = target with { UserSid = identity.User!.Value };
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            foreach (var (principalName, triggerName) in new[] { (true, false), (false, true), (true, true) })
            {
                var xml = XDocument.Parse(ElevatedStartup.TaskXml(current));
                var principal = xml.Descendants(ns + "Principal").Single().Element(ns + "UserId")!;
                var trigger = xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!;
                if (principalName) principal.Value = identity.Name;
                if (triggerName) trigger.Value = identity.Name;
                xml.Descendants().Where(e => e.Name.LocalName is
                    "Enabled" or "RunOnlyIfNetworkAvailable" or "RunOnlyIfIdle").Remove();
                xml.Root!.Element(ns + "RegistrationInfo")!.Add(new XElement(ns + "URI", "\\" + current.TaskName));
                Check(ElevatedStartup.Matches(xml.ToString(), current));

                var otherSid = new SecurityIdentifier(identity.User.Value == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18");
                string otherAccount = ((NTAccount)otherSid.Translate(typeof(NTAccount))).Value;
                principal.Value = otherAccount;
                Check(!ElevatedStartup.Matches(xml.ToString(), current));
                principal.Value = current.UserSid;
                trigger.Value = otherAccount;
                Check(!ElevatedStartup.Matches(xml.ToString(), current));
                trigger.Value = "";
                Check(!ElevatedStartup.Matches(xml.ToString(), current));
            }
        });
        Test("install verifies the task before removing plain startup", () =>
        {
            var store = new MemoryStore { Run = "old ordinary startup" };
            var result = ElevatedStartup.Apply(StartupOperation.Install, target, store);
            Check(result.Outcome == StartupOutcome.Applied && store.Run is null);
            Check(ElevatedStartup.Matches(store.Task, target));
            Check(store.Trace.IndexOf("ReadTask:verified") < store.Trace.IndexOf("WriteRun:remove"));
        });
        Test("repair repoints a moved portable exe", () =>
        {
            var store = new MemoryStore { Task = Changed(desired, "Command", @"C:\Old\Example.exe") };
            ElevatedStartup.Apply(StartupOperation.Repair, target, store);
            Check(ElevatedStartup.Matches(store.Task, target));
        });
        Test("repair re-enables a disabled task", () =>
        {
            var xml = XDocument.Parse(desired);
            xml.Descendants().Where(e => e.Name.LocalName == "Enabled").Last().Value = "false";
            var store = new MemoryStore { Task = xml.ToString() };
            ElevatedStartup.Apply(StartupOperation.Repair, target, store);
            Check(ElevatedStartup.Matches(store.Task, target));
        });
        Test("repair removes unconfigured trigger expiration, start bounds and delays", () =>
        {
            foreach (var (name, value) in new[] { ("StartBoundary", "2099-01-01T00:00:00"),
                ("EndBoundary", "2000-01-01T00:00:00"), ("Delay", "PT24H") })
            {
                var xml = XDocument.Parse(desired);
                var trigger = xml.Descendants().Single(e => e.Name.LocalName == "LogonTrigger");
                trigger.AddFirst(new XElement(trigger.Name.Namespace + name, value));
                var store = new MemoryStore { Task = xml.ToString() };
                Check(!ElevatedStartup.Matches(store.Task, target));
                Check(ElevatedStartup.Apply(StartupOperation.Repair, target, store).Outcome == StartupOutcome.Applied);
                Check(ElevatedStartup.Matches(store.Task, target));
            }
        });
        Test("concurrent helper cannot overwrite a transaction or its rollback snapshot", () =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var store = new MemoryStore { BeforeWrite = () => {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test coordination");
            } };
            var first = Task.Run(() => ElevatedStartup.Apply(StartupOperation.Install, target, store));
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "first helper did not acquire its lock");
                int reads = store.Trace.Count;
                var error = Error(() => ElevatedStartup.Apply(StartupOperation.Uninstall, target, store));
                Check(error.Message.Contains("in progress") && store.Trace.Count == reads);
            }
            finally { release.Set(); }
            Check(first.GetAwaiter().GetResult().Outcome == StartupOutcome.Applied);
            Check(ElevatedStartup.Matches(store.Task, target));
        });
        Test("install, repair and uninstall are idempotent", () =>
        {
            var store = new MemoryStore { Task = desired };
            Check(ElevatedStartup.Apply(StartupOperation.Install, target, store).Outcome == StartupOutcome.Unchanged);
            Check(ElevatedStartup.Apply(StartupOperation.Repair, target, store).Outcome == StartupOutcome.Unchanged);
            Check(store.Writes == 0);
            ElevatedStartup.Apply(StartupOperation.Uninstall, target, store);
            int writes = store.Writes;
            Check(store.Task is null && store.Run is null);
            Check(ElevatedStartup.Apply(StartupOperation.Uninstall, target, store).Outcome == StartupOutcome.Unchanged);
            Check(store.Writes == writes);
        });
        Test("uninstall clears both startup mechanisms", () =>
        {
            var store = new MemoryStore { Task = desired, Run = "legacy ordinary startup" };
            ElevatedStartup.Apply(StartupOperation.Uninstall, target, store);
            Check(store.Task is null && store.Run is null);
        });
        Test("a foreign task is never overwritten or removed", () =>
        {
            foreach (var operation in Enum.GetValues<StartupOperation>())
            {
                var store = new MemoryStore { Task = Changed(desired, "Source", "another-app"), Run = "previous" };
                Error(() => ElevatedStartup.Apply(operation, target, store));
                Check(store.Writes == 0 && store.Run == "previous");
            }
        });
        Test("permission-denied queries preserve uncertainty and make no writes", () =>
        {
            var store = new MemoryStore { FailRead = true, Run = "previous" };
            var error = Error(() => ElevatedStartup.Apply(StartupOperation.Install, target, store));
            Check(error.ToString().Contains("80070005") && error.Message.Contains("preflight"));
            Check(store.Writes == 0 && store.Run == "previous");
        });
        Test("partial registration failure restores the old task and Run entry", () =>
        {
            string old = Changed(desired, "Command", @"D:\Old\Example.exe");
            var store = new MemoryStore { Task = old, Run = "previous", FailWriteOnce = true };
            var error = Error(() => ElevatedStartup.Apply(StartupOperation.Repair, target, store));
            Check(store.Task == old && store.Run == "previous");
            Check(error.Message.Contains("previous startup restored"));
        });
        Test("bad registration readback removes the partial new task", () =>
        {
            var store = new MemoryStore { Run = "previous", CorruptWriteOnce = true };
            var error = Error(() => ElevatedStartup.Apply(StartupOperation.Install, target, store));
            Check(store.Task is null && store.Run == "previous");
            Check(error.Message.Contains("verify task"));
        });
        Test("Run cleanup failure rolls back an already registered task", () =>
        {
            var store = new MemoryStore { Run = "previous", FailRunOnce = true };
            Error(() => ElevatedStartup.Apply(StartupOperation.Install, target, store));
            Check(store.Task is null && store.Run == "previous");
        });
        Test("partial uninstall failure restores both old entries", () =>
        {
            var store = new MemoryStore { Task = desired, Run = "previous", FailDeleteOnce = true };
            Error(() => ElevatedStartup.Apply(StartupOperation.Uninstall, target, store));
            Check(store.Task == desired && store.Run == "previous");
        });
        Test("failed rollback is reported; independent Run restoration is still attempted", () =>
        {
            var store = new MemoryStore { Run = "previous", CorruptWriteOnce = true, FailDeleteAlways = true };
            var error = Error(() => ElevatedStartup.Apply(StartupOperation.Install, target, store));
            Check(error.Message.Contains("rollback incomplete"));
            Check(store.Trace.Contains("WriteRun:previous") && store.Run == "previous");
        });

        Test("configured delay and Normal priority survive generation and readback", () =>
        {
            foreach (int seconds in new[] { 0, 3, 17, 300 })
            {
                var configured = target with { Options = target.Options with { LogonDelaySeconds = seconds } };
                string xml = ElevatedStartup.TaskXml(configured);
                Check(ElevatedStartup.Matches(xml, configured));
                Check(XDocument.Parse(xml).Descendants().Single(e => e.Name.LocalName == "Priority").Value == "4");
                Check(!ElevatedStartup.Matches(Changed(xml, "Priority", "7"), configured));
                if (seconds != 0)
                {
                    Check(!ElevatedStartup.Matches(Changed(xml, "Delay", "PT1H"), configured));
                    Check(!ElevatedStartup.Matches(Changed(xml, "Delay", "bad-duration"), configured));
                }
                var store = new MemoryStore { Task = desired };
                ElevatedStartup.Apply(StartupOperation.Repair, configured, store);
                Check(ElevatedStartup.Matches(store.Task, configured));
            }
        });
        Test("helper accepts only bounded numeric delay and forwards the configured value", () =>
        {
            foreach (string invalid in new[] { "-1", "301", "1.5", "arbitrary.exe", "--show" })
                Error(() => ElevatedStartup.ParseHelper([ElevatedStartup.HelperSwitch, "install", target.UserSid, invalid], target.UserSid));
            var configured = target with { Options = target.Options with { LogonDelaySeconds = 17 } };
            var store = new MemoryStore { Task = ElevatedStartup.TaskXml(configured) };
            var result = ElevatedStartup.Request(StartupOperation.Install, configured, info => {
                Check(info.ArgumentList.SequenceEqual([ElevatedStartup.HelperSwitch, "install", target.UserSid, "17"]));
                Check(ElevatedStartup.HelperDelay(info.ArgumentList.ToArray()) == 17);
                return 0;
            }, store);
            Check(result.Outcome == StartupOutcome.Applied);
            foreach (int invalid in new[] { -1, 301 })
                Error(() => ElevatedStartup.Target(options with { LogonDelaySeconds = invalid }, target.Executable, target.UserSid));
        });
        Test("normal switch rolls back the task and both Run names together", () =>
        {
            var configured = target with { Options = target.Options with { LegacyRunValueName = "Legacy.Tests" } };
            var store = new MemoryStore { Task = desired, Run = "previous", Legacy = "legacy", FailLegacyOnce = true };
            Error(() => ElevatedStartup.Apply(StartupOperation.Plain, configured, store));
            Check(store.Task == desired && store.Run == "previous" && store.Legacy == "legacy");
            ElevatedStartup.Apply(StartupOperation.Plain, configured, store);
            Check(store.Task is null && store.Run == ElevatedStartup.PlainCommand(configured) && store.Legacy is null);
        });
        Test("overlong Run commands fail before changing either startup mechanism", () =>
        {
            var longTarget = ElevatedStartup.Target(options, @"D:\" + new string('a', 260) + @"\Example.exe", target.UserSid);
            var store = new MemoryStore { Task = desired, Run = "previous" };
            Error(() => ElevatedStartup.Apply(StartupOperation.Plain, longTarget, store));
            Check(store.Task == desired && store.Run == "previous" && store.Writes == 0);
        });
        Test("portable plain autostart creates shortcut and cleans up Run entry", () =>
        {
            var portableTarget = target with { Options = target.Options with { UseStartupShortcut = true } };
            var store = new MemoryStore { Run = "old run" };
            var result = ElevatedStartup.Apply(StartupOperation.Plain, portableTarget, store);
            Check(result.Outcome == StartupOutcome.Applied);
            Check(store.Shortcut == ElevatedStartup.PlainShortcut(portableTarget));
            Check(store.Run is null);
            Check(store.Task is null);
        });
        Test("portable plain autostart rollback restores previous shortcut and Run entry", () =>
        {
            var portableTarget = target with { Options = target.Options with { UseStartupShortcut = true } };
            var previous = new StartupShortcut(@"C:\old\Example.exe", "--old", @"C:\old-data");
            var store = new MemoryStore { Run = "previous run", Shortcut = previous, FailShortcutOnce = true };
            Error(() => ElevatedStartup.Apply(StartupOperation.Plain, portableTarget, store));
            Check(store.Run == "previous run" && store.Shortcut == previous);
        });
        Test("installing elevated task cleans up both Run entry and portable shortcut", () =>
        {
            var portableTarget = target with { Options = target.Options with { UseStartupShortcut = true } };
            var store = new MemoryStore { Run = "old run", Shortcut = ElevatedStartup.PlainShortcut(portableTarget) };
            var result = ElevatedStartup.Apply(StartupOperation.Install, portableTarget, store);
            Check(result.Outcome == StartupOutcome.Applied);
            Check(ElevatedStartup.Matches(store.Task, portableTarget));
            Check(store.Run is null && store.Shortcut is null);
        });
        Test("uninstalling clears task, Run entry, and portable shortcut", () =>
        {
            var portableTarget = target with { Options = target.Options with { UseStartupShortcut = true } };
            var store = new MemoryStore { Task = desired, Run = "old run", Shortcut = ElevatedStartup.PlainShortcut(portableTarget) };
            var result = ElevatedStartup.Apply(StartupOperation.Uninstall, portableTarget, store);
            Check(result.Outcome == StartupOutcome.Applied);
            Check(store.Task is null && store.Run is null && store.Shortcut is null);
        });
        Test("shortcut read failures stop every operation before any writes", () =>
        {
            foreach (var operation in Enum.GetValues<StartupOperation>())
            {
                var store = new MemoryStore { Task = desired, Run = "previous", FailShortcutRead = true };
                var error = Error(() => ElevatedStartup.Apply(operation, target, store));
                Check(error.Message.Contains("preflight") && ElevatedStartup.Classify(error) == StartupFailure.AccessDenied);
                Check(store.Writes == 0 && store.Task == desired && store.Run == "previous");
            }
        });
        Test("post-helper shortcut read failures preserve uncertainty", () =>
        {
            var store = new MemoryStore { Task = desired, FailShortcutRead = true };
            var result = ElevatedStartup.Request(StartupOperation.Install, target, _ => 0, store);
            Check(result.Outcome == StartupOutcome.Uncertain && result.Failure == StartupFailure.AccessDenied && store.Writes == 0);
        });
        Test("a stale shortcut working directory is repaired instead of reported unchanged", () =>
        {
            var portableTarget = target with { Options = options with { UseStartupShortcut = true } };
            var expected = ElevatedStartup.PlainShortcut(portableTarget);
            var store = new MemoryStore { Shortcut = expected with { WorkingDirectory = @"C:\old-data" } };
            Check(ElevatedStartup.Apply(StartupOperation.Plain, portableTarget, store).Outcome == StartupOutcome.Applied);
            Check(store.Shortcut == expected);
        });
        Test("post-helper readback checks the shortcut working directory", () =>
        {
            var portableTarget = target with { Options = options with { UseStartupShortcut = true } };
            var store = new MemoryStore { Shortcut = ElevatedStartup.PlainShortcut(portableTarget) with { WorkingDirectory = @"C:\old-data" } };
            Check(ElevatedStartup.Request(StartupOperation.Plain, portableTarget, _ => 0, store).Outcome == StartupOutcome.Uncertain);
        });
        Test("rollback reports an incorrectly restored shortcut working directory", () =>
        {
            var portableTarget = target with { Options = options with { UseStartupShortcut = true } };
            var previous = new StartupShortcut(@"C:\old\Example.exe", "--old", @"C:\old-data");
            var store = new MemoryStore { Shortcut = previous, FailShortcutOnce = true, CorruptShortcutWorkingDirectoryAtWrite = 2 };
            var error = Error(() => ElevatedStartup.Apply(StartupOperation.Plain, portableTarget, store));
            Check(error is StartupRollbackException && ElevatedStartup.Classify(error) == StartupFailure.RollbackIncomplete);
            Check(store.Shortcut?.WorkingDirectory != previous.WorkingDirectory);
        });
        Test("shortcut startup supports arguments beyond the Run command limit", () =>
        {
            var portableTarget = target with { Options = options with { UseStartupShortcut = true, PlainArguments = new string('x', 300) } };
            var store = new MemoryStore();
            Check(ElevatedStartup.Apply(StartupOperation.Plain, portableTarget, store).Outcome == StartupOutcome.Applied);
            Check(store.Shortcut == ElevatedStartup.PlainShortcut(portableTarget) && store.Run is null);
            Check(ElevatedStartup.Request(StartupOperation.Plain, portableTarget, _ => 0, store).Outcome == StartupOutcome.Applied);
        });
        Test("native shortcut absence is distinct from a corrupt or locked file", () => WithShortcutFiles((directory, store) =>
        {
            string path = Path.Combine(directory, options.AppId + ".lnk");
            Check(store.ReadShortcut(options.AppId) is null);
            Check(ElevatedStartup.Apply(StartupOperation.Uninstall, target, store).Outcome == StartupOutcome.Unchanged);
            byte[] corrupt = System.Text.Encoding.UTF8.GetBytes("invalid shortcut");
            File.WriteAllBytes(path, corrupt);
            Error(() => store.ReadShortcut(options.AppId));
            var error = Error(() => ElevatedStartup.Apply(StartupOperation.Uninstall, target, store));
            Check(error.Message.Contains("preflight") && store.Writes == 0 && File.ReadAllBytes(path).SequenceEqual(corrupt));
            var previous = new StartupShortcut(Environment.ProcessPath!, "--old", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            store.ShortcutFiles!.Write(options.AppId, previous);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Error(() => store.ReadShortcut(options.AppId));
                error = Error(() => ElevatedStartup.Apply(StartupOperation.Uninstall, target, store));
                Check(error.Message.Contains("preflight") && store.Writes == 0);
            }
            Check(ElevatedStartup.MatchesShortcut(store.ReadShortcut(options.AppId), previous));
        }));
        Test("native shortcut reads preserve long Unicode arguments and whitespace", () => WithShortcutFiles((_, store) =>
        {
            foreach (int length in new[] { 258, 259, 260, 300, 4096 })
            {
                var previous = new StartupShortcut(Environment.ProcessPath!, "  --name=\"中文 路径\" " + new string('x', length) + "  ",
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                string name = options.AppId + length;
                store.ShortcutFiles!.Write(name, previous);
                Check(ElevatedStartup.MatchesShortcut(store.ReadShortcut(name), previous), "Shortcut data truncated at argument length " + length);
            }
        }));
        Test("native shortcut rollback restores old arguments and working directory after a partial write", () => WithShortcutFiles((_, store) =>
        {
            var portableTarget = ElevatedStartup.Target(options with { UseStartupShortcut = true, ExecutableName = Path.GetFileName(Environment.ProcessPath!) },
                Environment.ProcessPath!, target.UserSid);
            var previous = new StartupShortcut(Environment.ProcessPath!, "--old=\"中文 路径\" " + new string('x', 300) + "  ",
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            store.ShortcutFiles!.Write(options.AppId, previous);
            var originalBytes = store.ReadShortcut(options.AppId)!.FileContentsBase64;
            store.Run = "previous run";
            store.FailShortcutOnce = true;
            var error = Error(() => ElevatedStartup.Apply(StartupOperation.Plain, portableTarget, store));
            Check(error is not StartupRollbackException && error.Message.Contains("previous startup restored"), error.ToString());
            Check(ElevatedStartup.MatchesShortcut(store.ReadShortcut(options.AppId), previous) && store.Run == "previous run" && store.Task is null);
            Check(store.ReadShortcut(options.AppId)!.FileContentsBase64 == originalBytes);
        }));

        Test("installed uninstall clears the legacy installed edition and preserves portable startup", () =>
        {
            var installed = ElevatedStartup.Target(options with { ExecutableName = "StashBay.exe" },
                @"C:\Programs\StashBay\StashBay.exe", target.UserSid);
            var legacyPackage = installed with { Executable = @"C:\Programs\StashBay-needs-dotnet10\StashBay.exe" };
            var portable = installed with { Executable = @"D:\Tools\StashBay.exe" };
            var store = new MemoryStore { Task = ElevatedStartup.TaskXml(portable) };
            Check(ElevatedStartup.Apply(StartupOperation.UninstallInstalled, installed, store).Outcome == StartupOutcome.Unchanged);
            Check(store.Writes == 0);
            store.Task = ElevatedStartup.TaskXml(legacyPackage);
            Check(ElevatedStartup.Apply(StartupOperation.UninstallInstalled, installed, store).Outcome == StartupOutcome.Applied);
            Check(store.Task is null);
        });

        Console.WriteLine($"{passed} isolated startup checks passed; no OS startup state accessed.");
    }
}

sealed class MemoryStore : IStartupStore
{
    public string? Task, Run, Legacy;
    public StartupShortcut? Shortcut;
    public StartupShortcutStore? ShortcutFiles;
    public bool FailLegacyOnce, FailShortcutOnce, FailShortcutRead;
    public int ShortcutWrites, CorruptShortcutWorkingDirectoryAtWrite;
    public bool FailRead, FailWriteOnce, CorruptWriteOnce, FailRunOnce, FailDeleteOnce, FailDeleteAlways;
    public int Writes;
    public Action? BeforeWrite;
    public List<string> Trace { get; } = [];
    public string? ReadTask(string name)
    {
        if (FailRead) throw new COMException("access denied", unchecked((int)0x80070005));
        Trace.Add(Task is null ? "ReadTask:missing" : "ReadTask:verified");
        return Task;
    }
    public void WriteTask(string name, string xml, string userSid)
    {
        BeforeWrite?.Invoke();
        Writes++;
        Task = xml;
        if (FailWriteOnce) { FailWriteOnce = false; throw new IOException("write failed after mutation"); }
        if (CorruptWriteOnce) { CorruptWriteOnce = false; Task = xml.Replace("HighestAvailable", "LeastPrivilege"); }
    }
    public void DeleteTask(string name)
    {
        Writes++;
        if (FailDeleteAlways) throw new IOException("delete denied");
        Task = null;
        if (FailDeleteOnce) { FailDeleteOnce = false; throw new IOException("delete failed after mutation"); }
    }
    public string? ReadRun(string valueName) => valueName == "Legacy.Tests" ? Legacy : Run;
    public void WriteRun(string valueName, string? command)
    {
        Writes++;
        Trace.Add("WriteRun:" + (command ?? "remove"));
        if (valueName == "Legacy.Tests")
        {
            Legacy = command;
            if (FailLegacyOnce) { FailLegacyOnce = false; throw new IOException("legacy write failed after mutation"); }
            return;
        }
        Run = command;
        if (FailRunOnce) { FailRunOnce = false; throw new IOException("registry write failed after mutation"); }
    }
    public StartupShortcut? ReadShortcut(string name)
    {
        if (FailShortcutRead) throw new UnauthorizedAccessException("shortcut read denied");
        return ShortcutFiles is not null ? ShortcutFiles.Read(name) : Shortcut;
    }
    public void WriteShortcut(string name, StartupShortcut? shortcut)
    {
        Writes++;
        ShortcutWrites++;
        Trace.Add("WriteShortcut:" + (shortcut is not null ? "create" : "remove"));
        if (ShortcutWrites == CorruptShortcutWorkingDirectoryAtWrite && shortcut is not null)
            shortcut = shortcut with { WorkingDirectory = @"C:\wrong-directory" };
        if (ShortcutFiles is not null) ShortcutFiles.Write(name, shortcut);
        else Shortcut = shortcut;
        if (FailShortcutOnce) { FailShortcutOnce = false; throw new IOException("shortcut write failed after mutation"); }
    }
}
