using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Principal;
using System.Text;
using Rifflo.Audio;

const string source = "kugou";
using var identity = WindowsIdentity.GetCurrent();
Console.WriteLine($"Windows {Environment.OSVersion.Version}, x64={Environment.Is64BitProcess}, " +
    $"elevated administrator={new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}");

var registrations = new WindowsAppRegistration();
var targets = registrations.GetExecutableTargets(source);
Require(targets.All(path => Path.IsPathFullyQualified(path) && File.Exists(path)) &&
    targets.Count == targets.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Resolved targets must exist and be unique.");
Console.WriteLine($"Shell targets ({targets.Count}): {string.Join(" | ", targets)}");

var readShortcuts = typeof(WindowsAppRegistration).GetMethod("ReadShortcuts", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
{
    var shortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readShortcuts.Invoke(null, [folder, source, shortcuts]);
    Console.WriteLine($"Shortcut targets in {folder} ({shortcuts.Count}): {string.Join(" | ", shortcuts)}");
}
Console.WriteLine($"Display-name query count: {registrations.GetExecutableTargets("酷狗音乐").Count}");

var pidList = new List<uint>();
foreach (var process in Process.GetProcessesByName("KuGou"))
{
    using (process)
        pidList.Add((uint)process.Id);
}
Require(pidList.Count > 0, "Start the installed KuGou app before running the identity checks.");

uint selfPid = (uint)Environment.ProcessId;
var observed = ProcessSnapshot.Read(source, pidList.Append(selfPid).Select(pid => new ActiveAudioSession(pid, false)).ToArray());
var trust = new KuGouExecutableTrust();
var ownImage = Environment.ProcessPath!;
var verified = new List<ProcessIdentity>();
foreach (var pid in pidList)
{
    if (!observed.TryGetValue(pid, out var process) || process.CreatedAt == 0)
        continue;

    bool valid = trust.IsTrusted(process);
    bool cached = trust.IsTrusted(process);
    bool wrongImage = trust.IsTrusted(process with { ImagePath = ownImage });
    bool wrongStart = trust.IsTrusted(process with { CreatedAt = process.CreatedAt - 1 });
    Console.WriteLine($"Live trust PID {pid}: valid={valid}, cached={cached}, mismatched={wrongImage}, " +
        $"wrongStart={wrongStart}, image={process.ImagePath}");
    Require(valid == cached && !wrongImage && !wrongStart, "Cached trust, loaded image, or PID creation check failed.");
    if (valid)
        verified.Add(process);
}
Require(verified.Count > 0, "The current session did not expose a trusted live KuGou image.");
Require(observed.TryGetValue(selfPid, out var self) && !trust.IsTrusted(self),
    "An unrelated running test executable must fail the KuGou identity check.");
Console.WriteLine("Unrelated running test executable rejected.");

var official = verified[0].ImagePath;
var probeDirectory = Path.Combine(Path.GetTempPath(), $"Rifflo-身份测试 空格 {Guid.NewGuid():N}");
var originalName = Path.Combine(probeDirectory, "KuGou.exe");
var movedName = Path.Combine(probeDirectory, "升级到 新版本.exe");
Directory.CreateDirectory(probeDirectory);
try
{
    // File-only probes exercise the same signature cache. The copies are never executed.
    var checkFile = typeof(KuGouExecutableTrust).GetMethod("CheckFile", BindingFlags.NonPublic | BindingFlags.Instance)!;
    File.Copy(official, originalName);
    Require(Check(originalName) && Check(originalName), "A valid executable in a custom path should be trusted and cached.");
    Console.WriteLine("Custom path with Unicode and spaces passed, including a cached check.");

    var stamp = File.GetLastWriteTimeUtc(originalName);
    var modified = File.ReadAllBytes(originalName);
    ChangeProductName(modified);
    File.WriteAllBytes(originalName, modified);
    File.SetLastWriteTimeUtc(originalName, stamp);
    Require(File.GetLastWriteTimeUtc(originalName) == stamp && !Check(originalName),
        "Changing the signed PE resource must invalidate old trust even at the same path, length and write time.");
    Console.WriteLine("Tampered signed product resource rejected at the old path and write time.");

    File.Copy(official, originalName, overwrite: true);
    Require(Check(originalName), "Restoring the official image must invalidate the failed cache entry.");
    File.Move(originalName, movedName);
    Require(Check(movedName), "The official executable should pass on the new path.");
    Console.WriteLine("Restored and moved official images passed the new trust checks.");

    File.Copy(ownImage, originalName);
    Require(!Check(originalName), "An unrelated image named KuGou.exe must be rejected.");
    Console.WriteLine("An unrelated executable with the same filename was rejected.");

    bool Check(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (bool)checkFile.Invoke(trust, [path, file])!;
    }
}
finally
{
    File.Delete(originalName);
    File.Delete(movedName);
    Directory.Delete(probeDirectory);
}

static void ChangeProductName(byte[] image)
{
    using var pe = new PEReader(new MemoryStream(image));
    int rva = pe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress;
    var section = pe.PEHeaders.SectionHeaders[pe.PEHeaders.GetContainingSectionIndex(rva)];
    var resources = image.AsSpan(section.PointerToRawData, section.SizeOfRawData);
    var label = Encoding.Unicode.GetBytes("ProductName\0");
    int found = resources.IndexOf(label);
    Require(found >= 0, "The temporary official copy has no ProductName block to modify.");
    int start = found + label.Length;
    int offset = resources[start..].IndexOf(Encoding.Unicode.GetBytes("KuGou\0"));
    Require(offset is >= 0 and <= 2, "The temporary official copy has an unexpected ProductName block.");
    resources[start + offset] = (byte)'N';
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
