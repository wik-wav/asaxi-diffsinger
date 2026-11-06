# Asaxi DiffSinger phonemizer for OpenUtau

A rule-based grapheme-to-phoneme plugin for **Asaxi** in **OpenUtau's DiffSinger** path. It is
not a dictionary: every word is derived from its spelling, so a word nobody has written yet
phonemises correctly the moment it is typed.

Its sibling [`asaxi-phonemizer`](https://github.com/wik-wav/asaxi-phonemizer) is the
*concatenative* (UTAU/CVVC) path and does not apply here.

## What it is

```
[DIFFS ASA]  DiffSinger Asaxi Phonemizer      (tag DIFFS ASA, language ASA)
```

It derives from OpenUtau's `DiffSingerG2pPhonemizer`, which means the host still reads the
singer's `dsdict-asa.yaml` for the **symbol table** — the phone names, their types, and which
of them the bank can actually sing — and merges that with a rule G2P that supplies the
**words**. Splitting the two is what makes an arbitrary-word phonemizer possible: the
dictionary needs one line per phone, never one per word.

`GetLangCode()` returns `asa`, and that one string is what makes the host rewrite every bare
phone the rules emit (`kx`) into the prefixed bank name (`asa/kx`) — the same mechanism
`[DIFFS EN]` uses to emit `en/*` in a multi-language bank.

## It does not need rebuilding when OpenUtau updates

The plugin is compiled against **`ref/OpenUtau.Core.Facade`**, a small reference assembly in
this repo that declares only the members the plugin binds to. Nothing at build time reads an
installed OpenUtau, so:

* a build machine needs no copy of OpenUtau at all;
* the built DLL binds at run time to whatever `OpenUtau.Core` the host has loaded, because the
  reference assembly's name is `OpenUtau.Core`;
* an OpenUtau update that keeps these API members needs **no rebuild**.

What that cannot survive is an actual **API change**. So `tests/Conformance` reflects over the
*real* `OpenUtau.Core` and checks that every base member still exists with the same shape and
that each override in the plugin still lands on a base definition above itself. **After any
OpenUtau update, run that test** — it is the difference between "it still loads" and "it silently
stopped overriding GetLangCode and now emits bare phones".

## The rules

The convention is documented in the Lozenge-T training package, a separate repository
(`documentation/ASA-phonemiser-IPA-documentation.md` there). It is implemented twice on purpose:

* `AsaxiG2p.cs` **in this repo** — the plugin;
* `asaxi_g2p.py` in the training package — the Python reference the corpus was built with.

`tests/asa_conformance.tsv` is generated from the Python side by that package's
`tools/make_asa_conformance.py`, and pins the two together: **355 words, including every word in
the coverage corpus**, must produce identical token strings. The fixture is committed so this
repo can test itself without the Python.

The parts worth knowing, because they are the ones a naive reading of the spelling gets wrong:

| spelling | phones | why |
|---|---|---|
| `kxe`, `txă`, `pxů` | `kx e`, `tx ay`, `px uw` | `x` after a stop is **aspiration**, not the letter `x` |
| `axa`, `ŕimxa`, `sxëka` | `a hh a`, `dx i m hh a` | after a vowel, `m` or `s` it is the letter `x` = /ɦ/ |
| `bwo`, `kjo`, `hja` | `bw o`, `ky o`, `hy o` | `w`/`j`/`x` after a consonant is one phone; `hj` is /ç/ |
| `á` | `ao` | one monophthong /ɑ/. The token is **named** `ao` by convention; it is not `a` + `o` |
| `å ă ë ỏ ő ů ýj` | `aw ay ey ow oy uw yj` | one phone each — **not** vowel + glide |
| `mao`, `baai`, `kae` | `m a o`, `b a a i`, `k a e` | plain vowel sequences are hiatus, two syllables — `a o`, never the `ao` token |
| `tte`, `sùggo` | `cl t e`, `s u cl g o` | a geminated stop is `cl` + the stop |
| `assa`, `am.ma` | `a s s a`, `a m m a` | any other doubled consonant keeps both tokens |
| `pigùmenn` | `p i g u m e xn` | a **word-final** `nn` is the uvular coda nucleus /ɴ/ |
| `siŕo`, `ńopa` | `S i dx o`, `ny o pa` | `si` is /ɕ/, `ni`/`ń` is /ɲ/ |
| `shivënwë` | `sh i v ey n w ey` | the fused imperative's `w` is an onset, not labialisation |
| `AP`, `SP` | `AP`, `SP` | pause phones: pass through bare, never `asa/`, never spelled as letters |

### Pause phones

`SP` (silence) and `AP` (breath) are language-neutral bank symbols and are handled as
**whole-lyric** matches: a note whose lyric is `AP` or `SP` in any case emits that token
unchanged, bare (no `asa/` prefix), and `IsValidSymbol`/`IsVowel` accept them so the host's
remapper and phonetic-hint path treat them as real symbols. They are deliberately **not**
part of the 103-token inventory, because they are not Asaxi phonemes.

## Download

If you only want to *use* it, **[`dist/ASAXI.DiffSinger.dll`](dist/ASAXI.DiffSinger.dll)** is the
built plugin — one file, no .NET SDK needed. Copy it into OpenUtau's `Plugins` folder and restart;
it appears as **DiffSinger Asaxi Phonemizer** (`DIFFS ASA`).

It is reproducible: `dotnet build -c Release` from this commit produces that exact file, byte for
byte, from any directory. `dist/SHA256SUMS` records the hash, and `dist/README.md` says how to check
it. If you rebuild and the bytes differ, the source changed — that is the point.

## Build

Needs the .NET **10** SDK. Nothing else — no OpenUtau, no NuGet packages, no Visual Studio.

```powershell
git clone https://github.com/wik-wav/asaxi-diffsinger
cd asaxi-diffsinger
dotnet build ref/OpenUtau.Core.Facade -c Release   # once: builds the reference assembly
dotnet build -c Release                             # the plugin
```

Output: `bin\Release\ASAXI.DiffSinger.dll`. Run the first command again only if you edit
`ref/OpenUtau.Core.Facade/Facade.cs`; otherwise it never needs repeating, whatever OpenUtau does.

The reference assembly is **not committed** — it is compiled from source on first build, which is
why a fresh clone cannot go straight to `dotnet build -c Release`.

## Install

Copy `bin\Release\ASAXI.DiffSinger.dll` into OpenUtau's `Plugins` folder and restart. It appears
as **DiffSinger Asaxi Phonemizer** (`DIFFS ASA`) in the phonemizer list.

## The bank side

The plugin cannot load without a `dsdict-asa.yaml` beside the singer, and a phone missing from
its `symbols:` block is dropped silently. The file is small and hand-writable — `symbols:` with a
line per phone, `entries:` empty:

```yaml
symbols:
- symbol: asa/kx
  type: aspirate
- symbol: asa/ao
  type: vowel
# ... one line per phone ...
entries: []          # deliberate: the words come from the rules, not from here
```

It is generated, together with `phonemes.json` and `languages.json`, by
`tools/make_asa_dsdict.py` in the Lozenge-T training package (not in this repo) from the single
shared inventory — 103 Asaxi phones plus `SP`/`AP`.

The model side must agree: `num_lang` must include `asa`, `use_lang_id` must be on, and every
`asa/*` name must exist in the model vocabulary. Names, ids and the dictionary all come from the
same 103-token inventory, so regenerating after a vocabulary change keeps them consistent.

## Test

```powershell
dotnet run --project tests/Conformance -c Release
```

**This one needs a real `OpenUtau.Core.dll`**, because binding to it is the thing being tested.
Point it at your install:

```powershell
dotnet run --project tests/Conformance -c Release -p:OpenUtauCorePath="C:\path\to\OpenUtau.Core.dll"
setx OPENUTAU_CORE "C:\path\to\OpenUtau.Core.dll"    # or set it once
```

Checks conformance against the Python reference, that the built DLL binds to the **real**
host assembly and still overrides every method it needs, and that the assembly exposes exactly
one instantiable `[Phonemizer]` type with `GetLangCode() == "asa"`. The plugin build itself does
not need this.

## Licence

[CC0 1.0 Universal](LICENSE) — a public-domain dedication. No attribution required, no conditions:
copy it, change it, ship it, sell it.

One provenance note that CC0 on this repository does not cover: the reference assembly under
`ref/` re-declares a handful of type and method signatures from
[OpenUtau](https://github.com/stakira/OpenUtau), which is MIT licensed (© 2014 StAkira). It
contains no OpenUtau code — only the names and shapes of the members the plugin binds to — so the
MIT notice applies to OpenUtau itself, not to this repository.
