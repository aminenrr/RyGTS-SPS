using System.Security.Cryptography;
// adcpatch: rewrites GT Sport (v1.69) AdHoc scripts for ryGT's offline features.
//   adcpatch roundtrip <in.adc> <out.adc>
//   adcpatch uikit     <UI_KIT.ADC> <out.adc> <openGrim.ad>
//   adcpatch livery2   <LIVERY2.ADC> <out.adc>
//   adcpatch thumbnail <THUMBNAIL.ADC> <out.adc>
//   adcpatch gallery   <GALLERY.ADC> <out.adc>
//   adcpatch grim      <GRIM.ADC> <out.adc> <grim.ad>
// The output is never larger than the input (the game reads scripts by their original size);
// it is padded with zeros up to the original size.
using GTAdhocToolchain.CodeGen;
using GTAdhocToolchain.Compiler;
using GTAdhocToolchain.Core;
using GTAdhocToolchain.Core.Instructions;
using GTAdhocToolchain.Disasm;
using GTAdhocToolchain.Preprocessor;
using Esprima;

static AdhocFile Load(string path, out AdhocSymbolMap map)
{
    var file = AdhocFile.ReadFromFile(path)[0];
    map = new AdhocSymbolMap();
    for (int i = 0; i < file.SymbolTable.Count; i++)
    {
        file.SymbolTable[i].Id = i;
        map.Symbols.TryAdd(file.SymbolTable[i].Name, file.SymbolTable[i]);
    }
    return file;
}

static void Save(AdhocFile file, AdhocSymbolMap map, string original, string output)
{
    AdhocStream.SymbolIdsByName = map.Symbols.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
    var gen = new AdhocCodeGen(file.TopLevelFrame, map);
    gen.Generate();
    string tmp = output + ".tmp";
    gen.SaveTo(tmp);
    var data = File.ReadAllBytes(tmp);
    File.Delete(tmp);
    long size = new FileInfo(original).Length;
    if (data.Length > size)
        throw new Exception($"patched script is {data.Length} bytes, more than the original {size}");
    var padded = new byte[size];
    data.CopyTo(padded, 0);
    File.WriteAllBytes(output, padded);
    Console.WriteLine($"{output}: {data.Length} of {size} bytes");
}

// Every symbol a frame uses gets an id in the target's table (new names are appended).
static void RegisterSymbols(object node, AdhocSymbolMap map, HashSet<object> seen)
{
    if (node == null || !seen.Add(node)) return;
    if (node is AdhocSymbol s)
    {
        if (!map.Symbols.TryGetValue(s.Name, out var existing))
        {
            existing = new AdhocSymbol(map.Symbols.Count, s.Name, s.HasHexEscape);
            map.Symbols.Add(s.Name, existing);
        }
        return;
    }
    if (node is string || node.GetType().IsPrimitive || node is Enum) return;
    if (node is System.Collections.IEnumerable list)
    {
        foreach (var item in list) RegisterSymbols(item, map, seen);
        return;
    }
    foreach (var prop in node.GetType().GetProperties())
    {
        if (prop.GetIndexParameters().Length != 0) continue;
        var t = prop.PropertyType;
        if (t.IsPrimitive || t == typeof(string) || t.IsEnum) continue;
        if (!(t.Namespace ?? "").StartsWith("GTAdhocToolchain") && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) continue;
        object v;
        try { v = prop.GetValue(node); } catch { continue; }
        RegisterSymbols(v, map, seen);
    }
}

static AdhocCodeFrame Compile(string sourcePath)
{
    var source = File.ReadAllText(sourcePath);
    var pre = new AdhocScriptPreprocessor();
    pre.SetCurrentFileName(Path.GetFileName(sourcePath));
    string preprocessed = pre.Preprocess(source);
    var errors = new CollectingErrorHandler();
    var parser = new AdhocAbstractSyntaxTree(new ParserOptions { ErrorHandler = errors });
    var program = parser.ParseScript(preprocessed, sourcePath);
    if (errors.Errors.Any())
        throw new Exception(string.Join("\n", errors.Errors.Select(e => $"{e.Description} line {e.LineNumber}")));
    var compiler = new AdhocScriptCompiler(12);
    return compiler.CompileScript(program, sourcePath);
}

static IEnumerable<(List<InstructionBase> list, int index, SubroutineBase sub)> Subroutines(List<InstructionBase> list)
{
    for (int i = 0; i < list.Count; i++)
    {
        if (list[i] is SubroutineBase sub)
        {
            yield return (list, i, sub);
            foreach (var inner in Subroutines(sub.CodeFrame.Instructions)) yield return inner;
        }
    }
}


// Removes ins[i..i+n) from a code frame and moves the frame's jump targets past it back by n.
static void RemoveRange(List<InstructionBase> ins, int i, int n)
{
    int end = i + n;
    ins.RemoveRange(i, n);
    foreach (var x in ins)
    {
        switch (x)
        {
            case InsJump j when j.JumpInstructionIndex >= end: j.JumpInstructionIndex -= n; break;
            case InsJumpIfFalse f when f.JumpIndex >= end: f.JumpIndex -= n; break;
            case InsJumpIfTrue t when t.JumpIndex >= end: t.JumpIndex -= n; break;
            case InsLogicalBase l when l.InstructionJumpIndex >= end: l.InstructionJumpIndex -= n; break;
            case InsTryCatch tc when tc.InstructionIndex >= end: tc.InstructionIndex -= n; break;
        }
    }
}

// Removes debug-build asserts (`if (GetVersionBuild() == "debug") DebugTool::Assert(...)`) and
// DebugTool::putHere logging from one code frame, fixing up the frame's jump targets. Makes room for
// patched code; both only do anything in debug builds or write logs.
static int StripDebug(List<InstructionBase> ins)
{
    static bool IsDebugTool(InstructionBase x, string fn) =>
        x is InsVariableEvaluation v && v.Path.Count == 4 && v.Path[0].Name == "main" && v.Path[1].Name == "DebugTool" && v.Path[2].Name == fn;
    int removed = 0;
    for (int i = 0; i < ins.Count; i++)
    {
        int end = -1;
        if (i + 4 < ins.Count && ins[i] is InsVariableEvaluation g && g.Path.Count == 1 && g.Path[0].Name == "GetVersionBuild" &&
            ins[i + 1] is InsCall c && c.ArgumentCount == 0 && ins[i + 2] is InsStringConst sc && sc.String.Name == "debug" &&
            ins[i + 3] is InsBinaryOperator op && op.Operator.Name == "__eq__" && ins[i + 4] is InsJumpIfFalse jf &&
            jf.JumpIndex > i + 5 && jf.JumpIndex <= ins.Count && ins[jf.JumpIndex - 1] is InsLeaveScope &&
            IsDebugTool(ins[i + 5], "Assert") && ins[jf.JumpIndex - 2] is InsPop && ins[jf.JumpIndex - 3] is InsCall)
        {
            bool plain = true;
            for (int k = i + 5; k < jf.JumpIndex - 3; k++)
                if (ins[k] is InsCall || ins[k] is InsJump || ins[k] is InsJumpIfFalse || ins[k] is InsJumpIfTrue || ins[k] is InsLogicalBase || ins[k] is SubroutineBase) plain = false;
            if (plain) end = jf.JumpIndex;
        }
        else if (IsDebugTool(ins[i], "putHere"))
        {
            int k = i + 1;
            while (k < ins.Count && ins[k] is not InsCall && ins[k] is not InsJump && ins[k] is not InsJumpIfFalse &&
                   ins[k] is not InsJumpIfTrue && ins[k] is not InsLogicalBase && ins[k] is not SubroutineBase) k++;
            if (k + 1 < ins.Count && ins[k] is InsCall && ins[k + 1] is InsPop) end = k + 2;
        }
        if (end < 0) continue;
        int n = end - i;
        // nothing may jump into the removed range (only to its start or past it)
        bool inside = ins.Any(x => (x is InsJump j && j.JumpInstructionIndex > i && j.JumpInstructionIndex < end) ||
                                   (x is InsJumpIfFalse f && f.JumpIndex > i && f.JumpIndex < end && !ReferenceEquals(x, ins[Math.Min(i + 4, ins.Count - 1)])) ||
                                   (x is InsJumpIfTrue t && t.JumpIndex > i && t.JumpIndex < end) ||
                                   (x is InsLogicalBase l && l.InstructionJumpIndex > i && l.InstructionJumpIndex < end) ||
                                   (x is InsTryCatch tc && tc.InstructionIndex > i && tc.InstructionIndex < end));
        if (inside) continue;
        RemoveRange(ins, i, n);
        removed++;
        i--;
    }
    return removed;
}

static int StripDebugAll(AdhocFile file)
{
    int n = StripDebug(file.TopLevelFrame.Instructions);
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions).ToList())
        n += StripDebug(sub.CodeFrame.Instructions);
    return n;
}


// Drops symbols nothing references any more (after code was replaced or removed); ids are renumbered.
static void PruneSymbols(AdhocFile file, AdhocSymbolMap map)
{
    var used = new AdhocSymbolMap();
    RegisterSymbols(file.TopLevelFrame, used, new HashSet<object>(ReferenceEqualityComparer.Instance));
    var kept = map.Symbols.Where(kv => used.Symbols.ContainsKey(kv.Key)).ToList();
    Console.WriteLine($"symbols: {map.Symbols.Count} -> {kept.Count}");
    map.Symbols.Clear();
    for (int i = 0; i < kept.Count; i++)
    {
        kept[i].Value.Id = i;
        map.Symbols.Add(kept[i].Key, kept[i].Value);
    }
}

string mode = args[0];
if (mode == "shaderindex")
{
    // shaderindex <game> <update or ""> <out.tsv>: hash, volume path, footer offset, code length
    // of every shader program in the game's files (the first file each one is found in).
    var idx = new List<string>();
    if (!string.IsNullOrEmpty(args[2])) idx.Add(Path.Combine(args[2], "gt.idx"));
    idx.Add(Path.Combine(args[1], "gt.idx"));
    var found = new Dictionary<ulong, string>();
    int files = 0;
    GameShaders.ForEachFile(idx, Path.Combine(Path.GetTempPath(), "rygt-shaderscan-" + Environment.ProcessId), (path, data) =>
    {
        if (++files % 2000 == 0) Console.WriteLine($"{files} files, {found.Count} programs");
        foreach (var (hash, footer, length) in GameShaders.Find(data))
            found.TryAdd(hash, $"{hash:x16}\t{path}\t{footer}\t{length}");
    });
    File.WriteAllLines(args[3], found.Values.OrderBy(x => x));
    Console.WriteLine($"{found.Count} programs in {files} files");
    return 0;
}

if (mode == "shadercodes")
{
    // shadercodes <game> <update or ""> <index.tsv> <hashes.txt> <out dir>: writes <hash>.bin for
    // each listed program, from its first instruction through its footer, from the player's own
    // game files (nothing leaves the PC).
    var idx = new List<string>();
    if (!string.IsNullOrEmpty(args[2])) idx.Add(Path.Combine(args[2], "gt.idx"));
    idx.Add(Path.Combine(args[1], "gt.idx"));
    var want = File.ReadAllLines(args[4]).Where(l => l.Trim().Length > 0).Select(l => Convert.ToUInt64(l.Trim(), 16)).ToHashSet();
    var index = new Dictionary<ulong, (string path, long footer, int length)>();
    foreach (var line in File.ReadLines(args[3]))
    {
        var f = line.Split('\t');
        ulong h = Convert.ToUInt64(f[0], 16);
        if (want.Contains(h)) index[h] = (f[1], long.Parse(f[2]), int.Parse(f[3]));
    }
    Directory.CreateDirectory(args[5]);
    string work = Path.Combine(Path.GetTempPath(), "rygt-shadercodes-" + Environment.ProcessId);
    int written = 0;
    try
    {
        foreach (var group in index.GroupBy(kv => kv.Value.path))
        {
            GameFiles.Extract(idx, new[] { group.Key }, work);
            string file = Path.Combine(work, group.Key.ToUpperInvariant().Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) continue;
            var data = File.ReadAllBytes(file);
            File.Delete(file);
            foreach (var (hash, e) in group)
            {
                int start = GameShaders.CodeStart(data, e.footer);
                if (start < 0) continue;
                File.WriteAllBytes(Path.Combine(args[5], $"{hash:x16}.bin"), data[start..(int)(e.footer + 32)]);
                written++;
            }
        }
    }
    finally { try { Directory.Delete(work, true); } catch { } }
    Console.WriteLine($"{written} of {want.Count} programs written");
    return 0;
}

if (mode == "shaderpack")
{
    // shaderpack <index.tsv> <identical.txt> <out.pack> <store dir>: the shader settings for
    // RyGT's Set up: the stored pipeline keys, the shader records that translate back exactly
    // (identical.txt: record name, program hash; from the renderer's SHADGT_REPLAY_TEST) and
    // where each record's program is in the game's files. No translated shaders.
    var index = new Dictionary<ulong, (string path, long footer, int length)>();
    foreach (var line in File.ReadLines(args[1]))
    {
        var f = line.Split('\t');
        index.TryAdd(Convert.ToUInt64(f[0], 16), (f[1], long.Parse(f[2]), int.Parse(f[3])));
    }
    var records = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadLines(args[2]))
    {
        var f = line.Split('\t');
        if (f.Length == 2 && index.ContainsKey(Convert.ToUInt64(f[1], 16))) records[f[0]] = Convert.ToUInt64(f[1], 16);
    }
    // A program goes in only with every permutation the store has of it (other.txt lists the
    // records that did not translate back exactly, next to identical.txt).
    string otherList = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2])), "replay_other.txt");
    if (File.Exists(otherList))
    {
        var incomplete = File.ReadLines(otherList).Select(l => l.Split('\t')).Where(f => f.Length == 2).Select(f => Convert.ToUInt64(f[1], 16)).ToHashSet();
        foreach (var name in records.Where(kv => incomplete.Contains(kv.Value)).Select(kv => kv.Key).ToList())
            records.Remove(name);
    }
    string store = args[4];
    var files = new List<string>();
    foreach (var file in Directory.EnumerateFiles(store))
    {
        string name = Path.GetFileName(file);
        if (name.EndsWith(".key") || (name.EndsWith(".meta") && records.ContainsKey(Path.GetFileNameWithoutExtension(name))))
            files.Add(file);
    }
    string fix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store).TrimEnd('\\', '/')), "CUSA03220.else-scope-fix");
    if (File.Exists(fix)) files.Add(fix);
    var programs = records.Values.Distinct().OrderBy(h => h).ToList();
    using var raw = new MemoryStream();
    using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
    {
        w.Write(programs.Count);
        foreach (var h in programs) { var e = index[h]; w.Write(h); w.Write(e.path); w.Write(e.footer); }
        w.Write(files.Count);
        foreach (var file in files.OrderBy(x => x))
        {
            var bytes = File.ReadAllBytes(file);
            w.Write(Path.GetFileName(file)); w.Write(bytes.Length); w.Write(bytes);
        }
    }
    raw.Position = 0;
    using var o = File.Create(args[3]);
    o.Write("RYGTSHD2"u8);
    using (var br = new System.IO.Compression.BrotliStream(o, System.IO.Compression.CompressionLevel.SmallestSize, true))
        raw.CopyTo(br);
    Console.WriteLine($"{args[3]}: {records.Count} shader records, {files.Count(f => f.EndsWith(".key"))} pipelines, {programs.Count} programs; {raw.Length / 1048576} MB in {o.Length / 1048576} MB");
    return 0;
}

if (mode == "shaderprep")
{
    // shaderprep <game> <update or ""> <shaders.pack> <cache dir>: what Set up does. Writes the
    // pack's shader settings into <cache dir>\CUSA03220-direct-sets, the renderer's store since
    // its stage descriptor sets (SHD-002; files already there are kept)
    // and copies each listed program out of the player's own game files into
    // <cache dir>\CUSA03220-programs; the renderer translates them on the splash screen.
    string game = args[1], update = args[2], cache = args[4];
    using var input = File.OpenRead(args[3]);
    var magic = new byte[8];
    input.ReadExactly(magic);
    if (!magic.AsSpan().SequenceEqual("RYGTSHD2"u8)) throw new Exception("not a RyGT shader pack");
    using var br = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
    using var r = new BinaryReader(br);
    int count = r.ReadInt32();
    var programs = new List<(ulong hash, string path, long footer)>();
    for (int i = 0; i < count; i++) programs.Add((r.ReadUInt64(), r.ReadString(), r.ReadInt64()));
    string store = Path.Combine(cache, "CUSA03220-direct-sets");
    Directory.CreateDirectory(store);
    int files = r.ReadInt32(), written = 0;
    for (int i = 0; i < files; i++)
    {
        string name = r.ReadString();
        var bytes = r.ReadBytes(r.ReadInt32());
        string target = name.EndsWith(".else-scope-fix") ? Path.Combine(cache, name) : Path.Combine(store, name);
        if (File.Exists(target)) continue;
        File.WriteAllBytes(target, bytes);
        written++;
    }
    string dir = Path.Combine(cache, "CUSA03220-programs");
    Directory.CreateDirectory(dir);
    var idx = new List<string>();
    if (!string.IsNullOrEmpty(update)) idx.Add(Path.Combine(update, "gt.idx"));
    idx.Add(Path.Combine(game, "gt.idx"));
    string work = Path.Combine(Path.GetTempPath(), "rygt-shaderprep-" + Environment.ProcessId);
    int copied = 0;
    try
    {
        foreach (var group in programs.Where(p => !File.Exists(Path.Combine(dir, $"{p.hash:x16}.bin"))).GroupBy(p => p.path))
        {
            string file;
            if (group.Key == "eboot.bin")
                file = new[] { update, game }.Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d, "eboot.bin")).FirstOrDefault(File.Exists);
            else
            {
                GameFiles.Extract(idx, new[] { group.Key }, work);
                file = Path.Combine(work, group.Key.ToUpperInvariant().Replace('/', Path.DirectorySeparatorChar));
            }
            if (file == null || !File.Exists(file)) continue;
            var data = File.ReadAllBytes(file);
            if (file.StartsWith(work)) File.Delete(file);
            foreach (var p in group)
            {
                if (p.footer + 32 > data.Length || BitConverter.ToUInt64(data, (int)p.footer + 16) != p.hash) continue;
                int start = GameShaders.CodeStart(data, p.footer);
                if (start < 0) continue;
                File.WriteAllBytes(Path.Combine(dir, $"{p.hash:x16}.bin"), data[start..(int)(p.footer + 32)]);
                copied++;
            }
        }
    }
    finally { try { Directory.Delete(work, true); } catch { } }
    Console.WriteLine($"{written} shader settings written, {copied} of {programs.Count} programs copied from the game files");
    return 0;
}

if (mode == "manifestprep")
{
    // manifestprep <game> <update or ""> <shaders dir> [<shipped list dir>...]: FORK-PRECOMPILE
    // (aminenrr/RyGTS-SPS). Reads the renderer's precompile manifest records (*.rec: inputs and
    // hashes, no code) from <shaders dir>\CUSA03220.manifest and any shipped list folders, and for
    // each record whose program code is not in <shaders dir>\CUSA03220.capture yet, copies it out
    // of the player's own game files as {hash:x16}-{XXH3_64(code):x16}.code (the renderer's
    // capture name; it pre-translates those on the splash screen). The program is found by the
    // OrbShdr footer hash; an index of the footers in the game files (built once, the slow part)
    // is kept in <shaders dir>\CUSA03220-shader-index.tsv.
    string game = args[1], update = args[2], cache = args[3];
    const string serial = "CUSA03220";
    var listDirs = new List<string> { Path.Combine(cache, serial + ".manifest") };
    listDirs.AddRange(args.Skip(4));
    string capture = Path.Combine(cache, serial + ".capture");
    string failedList = Path.Combine(cache, serial + "-manifestprep-failed.txt");
    var failedBefore = File.Exists(failedList)
        ? File.ReadAllLines(failedList).Select(l => l.Trim()).Where(l => l.Length != 0).ToHashSet()
        : new HashSet<string>();
    var needed = new Dictionary<(ulong hash, ulong xxh), uint>();
    int records = 0;
    foreach (var dir in listDirs.Where(Directory.Exists))
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.rec", SearchOption.AllDirectories))
        {
            // RYGTMAN1, u32 version, u32 hw stage, u32 sw stage, u64 hash, u64 code XXH3, u32 code words
            var head = new byte[40];
            try
            {
                using var f = File.OpenRead(file);
                if (f.Read(head, 0, head.Length) != head.Length) continue;
            }
            catch (IOException) { continue; }
            if (!head.AsSpan(0, 8).SequenceEqual("RYGTMAN1"u8)) continue;
            records++;
            ulong hash = BitConverter.ToUInt64(head, 20), xxh = BitConverter.ToUInt64(head, 28);
            uint words = BitConverter.ToUInt32(head, 36);
            if (words == 0 || words > (1u << 20)) continue;
            if (failedBefore.Contains($"{hash:x16}-{xxh:x16}")) continue;
            if (File.Exists(Path.Combine(capture, $"{hash:x16}-{xxh:x16}.code"))) continue;
            needed.TryAdd((hash, xxh), words);
        }
    }
    if (needed.Count == 0)
    {
        Console.WriteLine($"0 of 0 shader programs copied from the game files ({records} manifest records, all prepared)");
        return 0;
    }
    var volumes = new List<string>();
    if (!string.IsNullOrEmpty(update)) volumes.Add(Path.Combine(update, "gt.idx"));
    volumes.Add(Path.Combine(game, "gt.idx"));
    var gameDirs = new[] { update, game }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToList();
    string indexFile = Path.Combine(cache, serial + "-shader-index.tsv");
    var index = new Dictionary<ulong, (string path, long footer)>();
    if (File.Exists(indexFile))
    {
        foreach (var line in File.ReadLines(indexFile))
        {
            var f = line.Split('\t');
            if (f.Length == 3 && ulong.TryParse(f[0], System.Globalization.NumberStyles.HexNumber, null, out var h) &&
                long.TryParse(f[2], out var footer))
                index.TryAdd(h, (f[1], footer));
        }
    }
    else
    {
        Console.WriteLine("Indexing the shader programs in the game files (once, a few minutes)...");
        // Files on disk first (the executable and its modules), then every volume file.
        foreach (var dir in gameDirs)
        {
            var disk = new List<string>();
            if (File.Exists(Path.Combine(dir, "eboot.bin"))) disk.Add(Path.Combine(dir, "eboot.bin"));
            string modules = Path.Combine(dir, "sce_module");
            if (Directory.Exists(modules)) disk.AddRange(Directory.GetFiles(modules, "*.prx"));
            foreach (var file in disk)
            {
                string name = "disk:" + Path.GetRelativePath(dir, file).Replace('\\', '/');
                foreach (var (h, footer, _) in GameShaders.Find(File.ReadAllBytes(file)))
                    index.TryAdd(h, (name, footer));
            }
        }
        int scanned = 0;
        string scanTemp = Path.Combine(Path.GetTempPath(), "rygt-manifestprep-scan-" + Environment.ProcessId);
        try
        {
            GameShaders.ForEachFile(volumes, scanTemp, (path, data) =>
            {
                foreach (var (h, footer, _) in GameShaders.Find(data))
                    index.TryAdd(h, (path, footer));
                if (++scanned % 2000 == 0) Console.WriteLine($"{scanned} game files indexed, {index.Count} shader programs");
            });
        }
        finally { try { Directory.Delete(scanTemp, true); } catch { } }
        var tmp = indexFile + ".tmp";
        File.WriteAllLines(tmp, index.Select(kv => $"{kv.Key:x16}\t{kv.Value.path}\t{kv.Value.footer}"));
        File.Move(tmp, indexFile, true);
        Console.WriteLine($"{index.Count} shader programs indexed in {scanned} game files");
    }
    Directory.CreateDirectory(capture);
    int copied = 0, notFound = 0, mismatch = 0;
    var failedNow = new List<string>();
    string work = Path.Combine(Path.GetTempPath(), "rygt-manifestprep-" + Environment.ProcessId);
    try
    {
        var located = needed.Where(n => index.ContainsKey(n.Key.hash)).ToList();
        foreach (var n in needed.Where(n => !index.ContainsKey(n.Key.hash)))
        {
            notFound++;
            failedNow.Add($"{n.Key.hash:x16}-{n.Key.xxh:x16}");
        }
        var volumePaths = located.Select(n => index[n.Key.hash].path).Where(p => !p.StartsWith("disk:")).Distinct().ToList();
        var extracted = volumePaths.Count == 0 ? new HashSet<string>() : GameFiles.Extract(volumes, volumePaths, work);
        foreach (var group in located.GroupBy(n => index[n.Key.hash].path))
        {
            string file = null;
            if (group.Key.StartsWith("disk:"))
                file = gameDirs.Select(d => Path.Combine(d, group.Key.Substring(5).Replace('/', Path.DirectorySeparatorChar))).FirstOrDefault(File.Exists);
            else
                file = Path.Combine(work, group.Key.ToUpperInvariant().Replace('/', Path.DirectorySeparatorChar));
            byte[] data = file != null && File.Exists(file) ? File.ReadAllBytes(file) : null;
            foreach (var n in group)
            {
                long footer = index[n.Key.hash].footer;
                int start = data == null || footer + 32 > data.Length ? -1 : GameShaders.CodeStart(data, footer);
                long length = (long)n.Value * 4;
                if (start < 0 || start + length > data.Length ||
                    System.IO.Hashing.XxHash3.HashToUInt64(data.AsSpan(start, (int)length)) != n.Key.xxh)
                {
                    mismatch++;
                    failedNow.Add($"{n.Key.hash:x16}-{n.Key.xxh:x16}");
                    continue;
                }
                string target = Path.Combine(capture, $"{n.Key.hash:x16}-{n.Key.xxh:x16}.code");
                string temp = target + ".tmp";
                File.WriteAllBytes(temp, data.AsSpan(start, (int)length).ToArray());
                File.Move(temp, target, true);
                copied++;
            }
        }
    }
    finally { try { Directory.Delete(work, true); } catch { } }
    if (failedNow.Count != 0) File.AppendAllLines(failedList, failedNow);
    Console.WriteLine($"{copied} of {needed.Count} shader programs copied from the game files ({records} manifest records; " +
                      $"{notFound} not in the game files, {mismatch} different from the recorded code)");
    return 0;
}

if (mode == "extract")
{
    // extract <game folder> <update folder or ""> <out dir> <path>...: research helper.
    var idx = new List<string>();
    if (!string.IsNullOrEmpty(args[2])) idx.Add(Path.Combine(args[2], "gt.idx"));
    idx.Add(Path.Combine(args[1], "gt.idx"));
    var got = GameFiles.Extract(idx, args.Skip(4), args[3]);
    Console.WriteLine($"{got.Count} of {args.Length - 4} extracted");
    return 0;
}

if (mode == "all")
{
    // all <game folder> <update folder or ""> <output scripts folder>: extracts the five scripts from
    // the player's own game volumes, patches each (this program again, one mode each), and writes
    // them where the renderer (GTS-005) serves them from. The .ad sources sit next to this program.
    string game = args[1], update = args[2], output = args[3];
    string here = AppContext.BaseDirectory;
    var jobs = new (string mode, string path, string source)[]
    {
        ("uikit", "PROJECTS/GT7SP/UI_KIT/UI_KIT.ADC", "openGrim.ad"),
        ("livery2", "PROJECTS/GT7SP/LIVERY2/LIVERY2.ADC", null),
        ("thumbnail", "SCRIPTS/GT7SP/THUMBNAIL/THUMBNAIL.ADC", null),
        ("gallery", "PROJECTS/GT7SP/GALLERY/GALLERY.ADC", null),
        ("grim", "SCRIPTS/GT7SP/GRIM/GRIM.ADC", "grim.ad"),
    };
    string work = Path.Combine(Path.GetTempPath(), "rygt-scripts-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    var indices = new List<string>();
    if (!string.IsNullOrEmpty(update)) indices.Add(Path.Combine(update, "gt.idx"));
    indices.Add(Path.Combine(game, "gt.idx"));
    var found = GameFiles.Extract(indices, jobs.Select(j => j.path), work);
    Directory.CreateDirectory(output);
    int done = 0;
    foreach (var job in jobs)
    {
        string input = Path.Combine(work, job.path.Replace('/', Path.DirectorySeparatorChar));
        if (!found.Contains(job.path))
        {
            Console.WriteLine($"[X] {job.path} not found in the game files");
            continue;
        }
        string target = Path.Combine(output, Path.GetFileName(job.path));
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
        start.ArgumentList.Add(job.mode);
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(target);
        // The source's path goes into the compiled script: a bare name, as the tested scripts had.
        start.WorkingDirectory = here;
        if (job.source != null) start.ArgumentList.Add(job.source);
        using var process = System.Diagnostics.Process.Start(start);
        process.WaitForExit();
        if (process.ExitCode == 0 && File.Exists(target))
        {
            done++;
            Console.WriteLine($"[OK] {Path.GetFileName(job.path)}");
        }
        else
        {
            Console.WriteLine($"[X] {job.mode} failed (exit {process.ExitCode})");
        }
    }
    if (Environment.GetEnvironmentVariable("ADCPATCH_KEEP") != "1") { try { Directory.Delete(work, true); } catch { } } else Console.WriteLine($"originals kept in {work}");
    Console.WriteLine($"{done} of {jobs.Length} scripts patched into {output}");
    return done == jobs.Length ? 0 : 1;
}
if (mode == "roundtrip")
{
    var file = Load(args[1], out var map);
    Save(file, map, args[1], args[2]);
}
else if (mode == "uikit")
{
    // ProgressRoot.openGrim: offline, "Grim::requestLiveryUpload" is answered with a local id.
    var file = Load(args[1], out var map);
    var compiled = Compile(args[3]);
    var targets = Subroutines(file.TopLevelFrame.Instructions)
        .Where(x => x.sub.Name?.Name == "openGrim" && x.sub is InsMethodDefine).ToList();
    if (targets.Count != 1) throw new Exception($"found {targets.Count} openGrim methods");
    var module = targets[0].list; // ProgressRoot's body: the replacements are its methods
    foreach (var replacement in compiled.Instructions.OfType<InsModuleDefine>().Any()
                 ? Subroutines(compiled.Instructions).Where(x => x.sub is InsMethodDefine).Select(x => x.sub).ToList()
                 : new List<SubroutineBase>())
    {
        int index = module.FindIndex(x => x is InsMethodDefine m && m.Name?.Name == replacement.Name.Name);
        if (index < 0) throw new Exception($"ProgressRoot has no method {replacement.Name.Name}");
        RegisterSymbols(replacement, map, new HashSet<object>(ReferenceEqualityComparer.Instance));
        replacement.LineNumber = module[index].LineNumber;
        module[index] = replacement;
        Console.WriteLine($"replaced ProgressRoot.{replacement.Name.Name}");
    }
    Save(file, map, args[1], args[2]);
}
else if (mode == "livery2")
{
    // save(context): `if (CarEditTopRoot.is_online)` / `if (HelmetEditTopRoot.is_online)` before the
    // upload becomes `if (!false)`, so Save always takes the upload (and apply) path.
    var file = Load(args[1], out var map);
    var notSymbol = map.Symbols["__not__"];
    int patched = 0;
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions))
    {
        if (sub.Name?.Name != "save" || sub is not InsMethodDefine) continue;
        var ins = sub.CodeFrame.Instructions;
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i] is InsVariableEvaluation v && v.Path.Count > 0 && v.Path[^1].Name.EndsWith("EditTopRoot") &&
                ins[i + 1] is InsAttributeEvaluation a && a.Path.Count == 1 && a.Path[0].Name == "is_online" &&
                ins[i + 2].InstructionType == AdhocInstructionType.JUMP_IF_FALSE)
            {
                var b = new InsBoolConst(false) { LineNumber = v.LineNumber };
                var n = new InsUnaryOperator(notSymbol) { LineNumber = a.LineNumber };
                ins[i] = b;
                ins[i + 1] = n;
                patched++;
            }
        }
    }
    Console.WriteLine($"save methods patched: {patched}");

    // alias_AddDecal.onActivate: `if (type == USER && !DecalEditorRoot.getParent().is_online) return 2;`
    // turns user decals away offline. `.is_online` + `!` become `POP` + `false`.
    int gates = 0;
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions))
    {
        if (sub.Name?.Name != "onActivate" || sub is not InsMethodDefine) continue;
        var ins = sub.CodeFrame.Instructions;
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i] is InsAttributeEvaluation g && g.Path.Count == 1 && g.Path[0].Name == "getParent" &&
                ins[i + 2] is InsAttributeEvaluation a && a.Path.Count == 1 && a.Path[0].Name == "is_online" &&
                i + 3 < ins.Count && ins[i + 3] is InsUnaryOperator u && u.Operator.Name == "__not__" &&
                i > 0 && ins[i - 1] is InsVariableEvaluation v && v.Path[^1].Name == "DecalEditorRoot" &&
                i > 4 && ins[i - 2].InstructionType == AdhocInstructionType.LOGICAL_AND)
            {
                ins[i + 2] = new InsPop() { LineNumber = a.LineNumber };
                ins[i + 3] = new InsBoolConst(false) { LineNumber = u.LineNumber };
                gates++;
            }
        }
    }
    Console.WriteLine($"user decal gates patched: {gates}");
    Save(file, map, args[1], args[2]);
}
else if (mode == "thumbnail")
{
    // captureThumbnail: a composite thumbnail under 10 KB (32 KB at 2x) counts as a failed capture.
    // The livery report picture that the upload path captures comes out smaller here, so the size
    // floor is dropped: `file_size < 10 * 1024` becomes `file_size < 0 * 1024`.
    var file = Load(args[1], out var map);
    int patched = 0;
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions))
    {
        if (sub.Name?.Name != "captureThumbnail") continue;
        var ins = sub.CodeFrame.Instructions;
        for (int i = 1; i + 3 < ins.Count; i++)
        {
            if (ins[i] is InsIntConst k && (k.Value == 10 || k.Value == 32) &&
                ins[i - 1] is InsVariableEvaluation v && v.Path.Count == 1 && v.Path[0].Name == "file_size" &&
                ins[i + 1] is InsIntConst m && m.Value == 1024 &&
                ins[i + 3] is InsBinaryOperator op && op.Operator.Name == "__lt__")
            {
                k.Value = 0;
                patched++;
            }
        }
    }
    Console.WriteLine($"size checks patched: {patched}");
    Save(file, map, args[1], args[2]);
}
else if (mode == "gallery")
{
    // DecalStrategy.requestMyLibrary: the "My Decals" folder is listed only
    // `if (Player.login_util.isOnline() && is_online)`; it becomes `if (true)`.
    var file = Load(args[1], out var map);
    var top = file.TopLevelFrame.Instructions;
    int cls = top.FindIndex(x => x is InsClassDefine c && c.Name.Name == "DecalStrategy");
    int next = top.FindIndex(cls + 1, x => x is InsClassDefine);
    var method = (InsMethodDefine)top.Skip(cls).Take(next - cls).Single(x => x is InsMethodDefine m && m.Name?.Name == "requestMyLibrary");
    var ins = method.CodeFrame.Instructions;
    int at = ins.FindIndex(x => x is InsVariableEvaluation v && v.Path[^1].Name == "Player");
    if (at < 0 || ins[at + 4] is not InsLogicalAnd || ins[at + 5] is not InsVariableEvaluation iv || iv.Path[0].Name != "is_online" ||
        ins[at + 6] is not InsJumpIfFalse)
        throw new Exception("requestMyLibrary is not as expected");
    ins[at] = new InsBoolConst(true) { LineNumber = ins[at].LineNumber };
    RemoveRange(ins, at + 1, 5);
    Console.WriteLine("requestMyLibrary: user decal folder always listed");
    Save(file, map, args[1], args[2]);
}
else if (mode == "grim")
{
    // From grim.ad: GrimDecal.requestDecalList (the decal list comes from local SVG files) and
    // GrimLivery's ban checks (no server to ask; the garage loads liveries without them).
    var file = Load(args[1], out var map);
    var compiled = Compile(args[3]);
    var top = file.TopLevelFrame.Instructions;
    var jobs = new[] { ("GrimDecal", "requestDecalList"), ("GrimLivery", "liveryTest"), ("GrimLivery", "requestLiveryTest") };
    var modules = new HashSet<string>();
    foreach (var (module, name) in jobs)
    {
        var replacement = Subroutines(compiled.Instructions).Select(x => x.sub).Single(x => x is InsFunctionDefine f && f.Name.Name == name);
        int mod = top.FindIndex(x => x is InsModuleDefine m && m.Path[^1].Name == module);
        int index = mod < 0 ? -1 : top.FindIndex(mod, x => x is InsFunctionDefine f && f.Name.Name == name);
        if (index < 0) throw new Exception($"{module}.{name} not found");
        RegisterSymbols(replacement, map, new HashSet<object>(ReferenceEqualityComparer.Instance));
        replacement.LineNumber = top[index].LineNumber;
        top[index] = replacement;
        modules.Add(module);
        Console.WriteLine($"replaced {module}.{name}");
    }
    // room for them: GrimDecal's own debug asserts and logs (GrimLivery's replacements are smaller
    // than the originals)
    int stripped = 0;
    foreach (var module in new[] { "GrimDecal" })
    {
        int mod = top.FindIndex(x => x is InsModuleDefine m && m.Path[^1].Name == module);
        int nextModule = top.FindIndex(mod + 1, x => x is InsModuleDefine);
        foreach (var (_, _, sub) in Subroutines(top.GetRange(mod, (nextModule < 0 ? top.Count : nextModule) - mod)).ToList())
            stripped += StripDebug(sub.CodeFrame.Instructions);
    }
    Console.WriteLine($"debug asserts/logs removed: {stripped}");
    PruneSymbols(file, map);
    Save(file, map, args[1], args[2]);
}

return 0;
