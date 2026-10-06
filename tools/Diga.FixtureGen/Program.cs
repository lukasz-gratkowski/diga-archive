using Diga.Tests;

if (args.Length != 2) { Console.Error.WriteLine("Usage: Diga.FixtureGen input.mpg output.img (synthetic test image only)"); return 2; }
if (File.Exists(args[1])) { Console.Error.WriteLine("Destination already exists."); return 3; }
var payload = await File.ReadAllBytesAsync(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
await File.WriteAllBytesAsync(args[1], StorageFixtureBuilder.BuildMeihdfs(payload));
Console.WriteLine(Path.GetFullPath(args[1]));
return 0;
