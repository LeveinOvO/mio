using PortForwarder.Core;

if (args.Length == 3 && args[0] == "--native-host")
{ await NativeHostTests.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return; }

if (args.Length == 2 && args[0] == "--realm")
{ await RealmValidation.RunAsync(Path.GetFullPath(args[1])); return; }
if (args.Length == 2 && args[0] == "--realm-runtime")
{ await RealmRuntimeTests.RunAsync(Path.GetFullPath(args[1])); return; }
if (args.Length == 2 && args[0] == "--tray-asset")
{
    PortForwarder.Services.TrayIcon.VerifyIconFile(Path.GetFullPath(args[1]));
    Console.WriteLine("PASS: Published tray ICO loads successfully."); return;
}
if (args.Length != 0) throw new ArgumentException("Unknown test arguments.");

var count = 0;
count += RouteModelTests.Run();
void Check(bool value, string name)
{ if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
void Invalid(Action action, string name)
{
    try { action(); } catch (ArgumentException) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}
Check(RuleValidation.NormalizeIPv4(" 192.168.002.157 ") == "192.168.2.157", "IPv4 normalization");
Check(RuleValidation.NormalizeIPv4("0.0.0.0") == "0.0.0.0", "All-interface listener");
foreach (var address in new[] { "127.1", "123", "192.168.2.999", "256.1.1.1", "1.2.3", "1.2.3.4.5", "::1", "service.example.com", "1.2.3.-1", "" })
    Invalid(() => RuleValidation.NormalizeIPv4(address), "Invalid listener: " + address);
foreach (var target in new[] { "service.example.com", "LOCALHOST", "server-1.lan", "service.example.com." })
    Check(RuleValidation.NormalizeTarget(target) == target, "Preserve hostname: " + target);
Check(RuleValidation.NormalizeTarget("192.0.2.10") == "192.0.2.10", "IPv4 target");
Check(RuleValidation.NormalizeTarget("例子.测试").StartsWith("xn--", StringComparison.Ordinal), "IDN conversion");
foreach (var target in new[] { "http://service.example.com", "service.example.com:8080", "service.example.com/path", "a b", "bad\"name", "a;whoami", "-bad.com", "bad-.com", "a..b", "example.com..", "::1", "", "1.2.3.999", "*", "a_b.com", new string('a', 64) + ".com" })
    Invalid(() => RuleValidation.NormalizeTarget(target), "Invalid target: " + target);
Check(RuleValidation.ValidatePort(1) == 1 && RuleValidation.ValidatePort(65535) == 65535, "Port boundaries");
foreach (var port in new[] { 0d, -1d, 65536d, 8080.5, double.NaN, double.PositiveInfinity })
    Invalid(() => RuleValidation.ValidatePort(port), "Invalid port: " + port);
var trayType = typeof(PortForwarder.Services.TrayIcon);
Check(System.Runtime.InteropServices.Marshal.SizeOf(trayType.GetNestedType("NotifyIconData", System.Reflection.BindingFlags.NonPublic)!) == 976,
    "x64 tray native layout");
Check(System.Runtime.InteropServices.Marshal.SizeOf(trayType.GetNestedType("WindowClass", System.Reflection.BindingFlags.NonPublic)!) == 80,
    "x64 hidden window native layout");
count += await MonitoringTests.RunAsync();
count += ListRefreshTests.Run();
count += await SwitchingTests.RunAsync();
count += await TrayOnlyTests.RunAsync();
count += FirewallTests.Run();
count += FirewallStatusTests.Run();
count += RealmModelTests.Run();
count += ListenAddressTests.Run();
Console.WriteLine($"All {count} checks passed. No network configuration was modified.");
