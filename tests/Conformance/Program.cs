using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Asaxi;
using OpenUtau.Api;

namespace Conformance {
    /// <summary>
    /// Three things, in order of what they protect:
    ///
    /// 1. **Conformance.** The C# rules reproduce the Python reference on 355 words.
    /// 2. **Host binding.** The plugin was compiled against the reference assembly in
    ///    <c>ref/</c>, not against an installed OpenUtau, so this is what proves the built DLL
    ///    actually binds to the REAL <c>OpenUtau.Core</c> at run time. Every override must
    ///    land on a real base member (<c>GetBaseDefinition</c> reaches past our type).
    ///    Run this after every OpenUtau update: if it fails, an API changed and the plugin
    ///    needs a real fix — a rebuild alone will not help.
    /// 3. **Discovery.** Exactly one public <c>[Phonemizer]</c> type, instantiable.
    ///
    ///   dotnet run --project tests/Conformance -c Release
    /// </summary>
    static class Program {
        static int failures;

        static void Check(string what, bool ok, string detail = null) {
            Console.WriteLine("  {0,-58} {1}{2}", what, ok ? "ok" : "FAIL",
                detail == null ? "" : "  " + detail);
            if (!ok) {
                failures++;
            }
        }

        /// <summary>
        /// How would this voicebank cope with Asaxi? Reads the symbol table the way the host does
        /// (from the path its dsconfig declares, not always the root) and resolves every token
        /// through the same ladder the phonemizer uses.
        /// </summary>
        static int BankReport(string bankDir) {
            var dsconfig = Path.Combine(bankDir, "dsconfig.yaml");
            var phonemes = "phonemes.json";
            if (File.Exists(dsconfig)) {
                foreach (var line in File.ReadAllLines(dsconfig)) {
                    var t = line.Trim();
                    if (t.StartsWith("phonemes:", StringComparison.Ordinal)) {
                        var v = t.Substring("phonemes:".Length).Trim().Trim('"', '\'');
                        if (v.Length > 0) {
                            phonemes = v.Replace('/', Path.DirectorySeparatorChar);
                        }
                    }
                }
            }
            var table = Path.Combine(bankDir, phonemes);
            if (!File.Exists(table)) {
                Console.Error.WriteLine("no symbol table at " + table);
                return 2;
            }
            var names = new HashSet<string>(AsaxiG2p.LoadNameToId(table).Keys);
            bool Has(string n) => names.Contains(n);

            var inventory = AsaxiG2p.Vowels.Concat(AsaxiG2p.Nuclei).Concat(AsaxiG2p.Consonants)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            int direct = 0, viaLadder = 0, viaSplit = 0, lost = 0;
            var lostNames = new List<string>();
            foreach (var token in inventory) {
                if (Has("asa/" + token)) {
                    direct++;
                    continue;
                }
                var resolved = AsaxiG2p.ResolveBankName("asa/" + token, Has);
                if (resolved != null) {
                    viaLadder++;
                    continue;
                }
                if (AsaxiG2p.TrySplitCluster(token, out var h, out var g)
                    && AsaxiG2p.ResolveBankName("asa/" + h, Has) != null
                    && AsaxiG2p.ResolveBankName("asa/" + g, Has) != null) {
                    viaSplit++;
                    continue;
                }
                lost++;
                if (lostNames.Count < 25) {
                    lostNames.Add(token);
                }
            }
            Console.WriteLine("bank: " + Path.GetFileName(bankDir));
            Console.WriteLine("  symbol table: {0}  ({1} names)", phonemes, names.Count);
            Console.WriteLine("  asa/* present      : {0}", direct);
            Console.WriteLine("  resolved by ladder : {0}", viaLadder);
            Console.WriteLine("  resolved by split  : {0}", viaSplit);
            Console.WriteLine("  unresolvable       : {0}  {1}", lost,
                lost == 0 ? "" : string.Join(" ", lostNames));
            return lost == 0 && viaLadder + direct > 0 ? 0 : 1;
        }

        static int Main(string[] args) {
            var bankArg = Array.IndexOf(args, "--bank");
            if (bankArg >= 0 && bankArg + 1 < args.Length) {
                return BankReport(args[bankArg + 1]);
            }
            var here = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            var tsv = Path.Combine(here, "asa_conformance.tsv");
            if (!File.Exists(tsv)) {
                tsv = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "asa_conformance.tsv"));
            }
            if (!File.Exists(tsv)) {
                Console.Error.WriteLine("conformance table not found: " + tsv);
                return 2;
            }

            // ---------------------------------------------------------------- conformance
            var g2p = new AsaxiG2p();
            int total = 0, bad = 0;
            var shown = new List<string>();
            foreach (var line in File.ReadAllLines(tsv).Skip(1)) {
                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }
                var parts = line.Split('\t');
                if (parts.Length < 2) {
                    continue;
                }
                total++;
                var want = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var got = g2p.Query(parts[0]) ?? Array.Empty<string>();
                if (!want.SequenceEqual(got)) {
                    bad++;
                    if (shown.Count < 20) {
                        shown.Add(string.Format("    {0,-14} want {1,-30} got {2}",
                            parts[0], string.Join(" ", want), string.Join(" ", got)));
                    }
                }
            }
            Console.WriteLine("conformance: {0}/{1} words", total - bad, total);
            foreach (var s in shown) {
                Console.WriteLine(s);
            }
            if (bad > 0) {
                failures++;
            }

            // ---------------------------------------------------------------- host binding
            Console.WriteLine();
            Console.WriteLine("host binding (real OpenUtau.Core {0}):",
                typeof(Phonemizer).Assembly.GetName().Version);
            var asm = typeof(Phonemizer).Assembly;

            var baseType = typeof(OpenUtau.Core.DiffSinger.DiffSingerG2pPhonemizer);
            Check("DiffSingerG2pPhonemizer is public", baseType.IsPublic);
            Check("DiffSingerG2pPhonemizer : Phonemizer", typeof(Phonemizer).IsAssignableFrom(baseType));
            Check("IG2p is public", typeof(IG2p).IsPublic);
            foreach (var m in typeof(IG2p).GetMethods()) {
                Check("IG2p." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")",
                    m.DeclaringType == typeof(IG2p));
            }
            Check("PhonemizerAttribute takes (name, tag, author?, language?)",
                typeof(PhonemizerAttribute).GetConstructors().Any(c => {
                    var ps = c.GetParameters();
                    return ps.Length == 4 && ps[0].ParameterType == typeof(string)
                        && ps[3].ParameterType == typeof(string)
                        && ps[3].IsOptional;
                }));

            // every override must reach a base definition above our own type
            var mine = typeof(DiffSingerAsaxiPhonemizer);
            var expected = new[] {
                "GetLangCode", "GetDictionaryName", "LoadBaseG2p",
                "GetBaseG2pVowels", "GetBaseG2pConsonants", "LoadG2p",
            };
            foreach (var name in expected) {
                var method = mine.GetMethods(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.DeclaringType == mine);
                var isOverride = method != null && method.GetBaseDefinition() != method;
                Check(name + " overrides a real base member", isOverride);
            }

            // the facade also declares IsPhonemeSupported (non-virtual, called not overridden)
            var isSupported = baseType.GetMethod("IsPhonemeSupported",
                BindingFlags.Instance | BindingFlags.NonPublic);
            // Process must override the host's, or hints can never be rewritten
            // hints are computed in SetUp, so that is the one that must be overridden
            var setUp = mine.GetMethod("SetUp", BindingFlags.Instance | BindingFlags.Public);
            Check("SetUp overrides the host's (where hints are read)",
                setUp != null && setUp.DeclaringType == mine && setUp.GetBaseDefinition() != setUp);

            Check("IsPhonemeSupported exists on the base",
                isSupported != null && isSupported.ReturnType == typeof(bool)
                && isSupported.GetParameters().Length == 1);

            // asm is the host assembly only because the plugin bound to it; if the plugin had
            // resolved its own copy, LoadBaseG2p would still work but this identity would differ
            Check("plugin bound to the host assembly instance",
                ReferenceEquals(typeof(DiffSingerAsaxiPhonemizer).BaseType.Assembly, asm));

            // ---------------------------------------------------------------- discovery
            Console.WriteLine();
            var pluginAsm = typeof(AsaxiG2p).Assembly;
            var marked = pluginAsm.GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract)
                .Select(t => (type: t, attr: t.GetCustomAttribute<PhonemizerAttribute>()))
                .Where(x => x.attr != null)
                .ToList();
            Check("exactly one [Phonemizer] type", marked.Count == 1);
            foreach (var (type, attr) in marked) {
                Console.WriteLine("    {0}  tag={1}  lang={2}", attr.Name, attr.Tag, attr.Language);
                var instance = (Phonemizer)Activator.CreateInstance(type);
                Check("instantiable", instance != null);
                Check("language code is asa", ((DiffSingerAsaxiPhonemizer)instance).GetLangCode() == "asa",
                    ((DiffSingerAsaxiPhonemizer)instance).GetLangCode());
                // the menu groups by this verbatim; "ASA" would give the plugin its own heading
                Check("menu language groups with the Asaxi plugin", attr.Language == "ASA: Asaxi",
                    attr.Language);
            }

            // ---------------------------------------------------------------- inventory
            var emitted = File.ReadAllLines(tsv).Skip(1)
                .Where(l => l.Contains('\t'))
                .SelectMany(l => l.Split('\t')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct().ToArray();
            var undeclared = emitted.Where(s => !g2p.IsValidSymbol(s)).ToArray();
            Console.WriteLine();
            Check("every emitted symbol is declared", undeclared.Length == 0,
                undeclared.Length == 0 ? null : string.Join(" ", undeclared));
            Check("inventory is 104", emitted.Length == 104, emitted.Length.ToString());

            // ---------------------------------------------------------------- cluster splitting
            Console.WriteLine();
            string[] rep;
            Check("kw splits to k + w",
                AsaxiG2p.TrySplitCluster("kw", out var s1, out var g1) && s1 == "k" && g1 == "w");
            Check("ky splits to k + j",
                AsaxiG2p.TrySplitCluster("ky", out var s2, out var g2) && s2 == "k" && g2 == "j");
            Check("nyw splits to ny + w",
                AsaxiG2p.TrySplitCluster("nyw", out var s3, out var g3) && s3 == "ny" && g3 == "w");
            Check("ny does NOT split (it is /ɲ/)", !AsaxiG2p.TrySplitCluster("ny", out _, out _));
            Check("hy does NOT split (it is /ç/)", !AsaxiG2p.TrySplitCluster("hy", out _, out _));

            // a bank WITH the cluster: passes through untouched
            var rich = new HashSet<string> { "asa/ky", "asa/k", "asa/j" };
            Check("bank that has asa/ky keeps it",
                !AsaxiG2p.TryDegrade("asa/ky", s => rich.Contains(s), null, out rep));

            // a bank WITHOUT it but with both halves: degrades
            var poor = new HashSet<string> { "asa/k", "asa/j" };
            Check("bank without asa/ky degrades to asa/k + asa/j",
                AsaxiG2p.TryDegrade("asa/ky", s => poor.Contains(s), null, out rep)
                && rep.Length == 2 && rep[0] == "asa/k" && rep[1] == "asa/j",
                rep == null ? "(no split)" : string.Join(" ", rep));

            // aspiration: stop + release, ALWAYS, because no bank of this family has a
            // trained aspirated series. The release is the hh token, whose placeholder is en/hh.
            Check("kx splits to k + hh (aspiration, not a glide)",
                AsaxiG2p.TrySplitCluster("kx", out var s4, out var g4) && s4 == "k" && g4 == "hh");
            Check("MustSplit: aspiration always, palatalisation only as fallback",
                AsaxiG2p.MustSplit("kx") && AsaxiG2p.MustSplit("px") && !AsaxiG2p.MustSplit("ky"));
            var withAspiration = new HashSet<string> { "asa/kx", "asa/k", "asa/hh" };
            Check("aspiration splits when asa/kx is only an alias",
                AsaxiG2p.TryDegrade("asa/kx", s => withAspiration.Contains(s), (c, b) => true, out rep)
                && rep.Length == 2 && rep[0] == "asa/k" && rep[1] == "asa/hh",
                rep == null ? "(no split)" : string.Join(" ", rep));

            // a placeholder alias: the bank HAS asa/bw, but it is the same id as asa/b, so it
            // carries none of the labialisation and must still be split
            var aliased = new HashSet<string> { "asa/bw", "asa/b", "asa/w" };
            Check("placeholder alias (asa/bw == asa/b) still splits",
                AsaxiG2p.TryDegrade("asa/bw", s => aliased.Contains(s),
                                    (c, b) => c == "asa/bw" && b == "asa/b", out rep)
                && rep.Length == 2 && rep[0] == "asa/b" && rep[1] == "asa/w",
                rep == null ? "(no split)" : string.Join(" ", rep));

            // a REAL labialised phone (its own id) must be kept
            var genuine = new HashSet<string> { "asa/gw", "asa/g", "asa/w" };
            Check("a genuine asa/gw is kept, not split",
                !AsaxiG2p.TryDegrade("asa/gw", s => genuine.Contains(s), (c, b) => false, out rep));

            // a bank with a REAL trained aspirated stop keeps it
            var trained = new HashSet<string> { "asa/kx", "asa/k", "asa/hh" };
            Check("a genuine asa/kx is kept, not split",
                !AsaxiG2p.TryDegrade("asa/kx", s => trained.Contains(s), (c, b) => false, out rep));

            // ...but an aliased one still splits (asa/kx pointing at asa/k)
            Check("an aliased asa/kx still splits",
                AsaxiG2p.TryDegrade("asa/kx", s => trained.Contains(s),
                                    (c, b) => c == "asa/kx" && b == "asa/k", out rep)
                && rep.Length == 2 && rep[0] == "asa/k" && rep[1] == "asa/hh",
                rep == null ? "(no split)" : string.Join(" ", rep));

            // and with no id table at all, aspiration still splits rather than guessing wrong
            Check("aspiration splits when the id table is unavailable",
                AsaxiG2p.TryDegrade("asa/kx", s => trained.Contains(s), null, out rep));

            // a bank with neither half: leave it alone, the host reports it as before
            Check("bank without the glide does not degrade",
                !AsaxiG2p.TryDegrade("asa/ky", s => s == "asa/k", null, out rep));

            // ---------------------------------------------------------------- foreign banks
            // Modelled on a real multi-language bank (HaiameSen_DS_Neo): every name prefixed,
            // no bare names at all, no asa/*. The ladder has to find each phone by its own
            // spelling under whichever language carries it.
            Console.WriteLine();
            var foreign = new HashSet<string> {
                "en/a", "en/p", "en/b", "en/k", "en/t", "en/d", "en/g", "en/m", "en/n", "en/l",
                "en/s", "en/z", "en/f", "en/v", "en/w", "en/y", "en/h", "en/hh", "en/ch", "en/sh",
                "en/zh", "en/jh", "en/th", "en/dh", "en/tcl", "en/q", "en/ng", "en/nn", "en/er",
                "en/ao", "en/ay", "en/aw", "en/ax", "en/ey", "en/ih", "en/ow", "en/oy", "en/uw",
                "en/iy", "en/e", "en/o", "ja/u", "ja/i", "ja/ny", "pl/S", "ja/ts", "en/dz",
                "en/dx", "SP", "AP",
            };
            bool Has(string n) => foreign.Contains(n);
            Check("asa/a  -> en/a on a bank with no asa/*", AsaxiG2p.ResolveBankName("asa/a", Has) == "en/a");
            Check("asa/p  -> en/p", AsaxiG2p.ResolveBankName("asa/p", Has) == "en/p");
            Check("asa/j  -> en/y (the glide is spelled y)", AsaxiG2p.ResolveBankName("asa/j", Has) == "en/y");
            Check("asa/S  -> pl/S (/ɕ/, not en/sh)", AsaxiG2p.ResolveBankName("asa/S", Has) == "pl/S");
            Check("asa/ts -> ja/ts", AsaxiG2p.ResolveBankName("asa/ts", Has) == "ja/ts");
            Check("asa/ny -> ja/ny", AsaxiG2p.ResolveBankName("asa/ny", Has) == "ja/ny");
            Check("asa/u  -> ja/u (en has no /ɯ/)", AsaxiG2p.ResolveBankName("asa/u", Has) == "ja/u");
            Check("asa/hh -> en/hh", AsaxiG2p.ResolveBankName("asa/hh", Has) == "en/hh");
            Check("asa/bw has no name there",
                AsaxiG2p.ResolveBankName("asa/bw", Has) == null);

            // a bare hint symbol must find the bank's own name for it
            var hintBank = new HashSet<string> { "ja/k", "ja/a", "en/y", "ja/w", "SP", "AP" };
            bool HasHint(string n) => hintBank.Contains(n);
            Check("hint `k` -> ja/k on a prefixed-only bank",
                AsaxiG2p.ResolveHintSymbol("k", HasHint) == "ja/k");
            Check("hint `a` -> ja/a", AsaxiG2p.ResolveHintSymbol("a", HasHint) == "ja/a");
            Check("hint `y` -> en/y (it is the spelling of `j`)",
                AsaxiG2p.ResolveHintSymbol("y", HasHint) == "en/y");
            Check("hint `sil` -> SP", AsaxiG2p.ResolveHintSymbol("sil", HasHint) == "SP");
            Check("unknown hint symbol resolves to nothing",
                AsaxiG2p.ResolveHintSymbol("zzz", HasHint) == null);
            Check("a hint may still be written in the bank's own names",
                AsaxiG2p.ResolveHintSymbol("ja/k", HasHint) == "ja/k");

            // a bank with Mandarin `q` (/tɕʰ/) but no glottal stop must NOT borrow zh/q
            var wrongQ = new HashSet<string> {
                "en/a", "zh/q", "zh/ch", "ja/ch", "es/ts",
            };
            bool HasWrongQ(string n) => wrongQ.Contains(n);
            Check("asa/q never resolves to zh/q (/tɕʰ/)",
                AsaxiG2p.ResolveBankName("asa/q", HasWrongQ) == null,
                AsaxiG2p.ResolveBankName("asa/q", HasWrongQ) ?? "(none)");
            Check("asa/ch never resolves to ja/ch (/tɕ/)",
                AsaxiG2p.ResolveBankName("asa/ch", HasWrongQ) == null);
            Check("asa/hy never resolves to a look-alike",
                AsaxiG2p.ResolveBankName("asa/hy", HasWrongQ) == null);

            // ...and that the cluster then splits into two names the bank does have
            Check("asa/bw splits to en/b + en/w on that bank",
                !Has("asa/bw")
                && AsaxiG2p.TrySplitCluster("bw", out var fb, out var fg)
                && AsaxiG2p.ResolveBankName("asa/" + fb, Has) == "en/b"
                && AsaxiG2p.ResolveBankName("asa/" + fg, Has) == "en/w");

            // ---------------------------------------------------------------- pause phones
            Console.WriteLine();
            Check("AP phonemises to AP", g2p.Query("AP")?.SequenceEqual(new[] { "AP" }) == true,
                string.Join(" ", g2p.Query("AP") ?? new string[0]));
            Check("SP phonemises to SP", g2p.Query("SP")?.SequenceEqual(new[] { "SP" }) == true,
                string.Join(" ", g2p.Query("SP") ?? new string[0]));
            Check("lower-case ap/sp also resolve", 
                g2p.Query("ap")?.SequenceEqual(new[] { "AP" }) == true
                && g2p.Query("sp")?.SequenceEqual(new[] { "SP" }) == true);
            Check("a word containing ap/sp is untouched",
                string.Join(" ", g2p.Query("appa")) == "a cl p a"
                && string.Join(" ", g2p.Query("spa")) == "s p a",
                string.Join(" ", g2p.Query("spa")));
            Check("AP/SP are valid and vowel-placed",
                g2p.IsValidSymbol("AP") && g2p.IsVowel("AP")
                && g2p.IsValidSymbol("SP") && g2p.IsVowel("SP"));
            Check("AP/SP pass through a phonetic hint",
                g2p.UnpackHint("asa/kx AP SP")?.SequenceEqual(new[] { "AP", "SP" }) == true,
                string.Join(" ", g2p.UnpackHint("asa/kx AP SP") ?? new string[0]));
            Check("AP/SP are not in the 104-token inventory",
                !emitted.Contains("AP") && !emitted.Contains("SP"));

            Console.WriteLine();
            Console.WriteLine("RESULT: " + (failures == 0 ? "pass" : "FAIL (" + failures + ")"));
            return failures == 0 ? 0 : 1;
        }
    }
}
