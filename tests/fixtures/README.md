# Test fixtures

`tests/fixtures/` holds byte-preserving inputs shared by `tests/BackFiller.tests`. Tests locate this directory by walking parents of `AppContext.BaseDirectory` until `BackFiller.slnx` is found, then joining `tests/fixtures`. That works from the test runner output directory and from CI. Paths are not absolute developer-machine paths.

## Layout

- `articles/` — InterNetNews article samples. See below.
- `SabctoolsYEnc/` — upstream yEnc bodies framed as NNTP responses. Provenance and the file list are in [SabctoolsYEnc/README.md](SabctoolsYEnc/README.md).

A file under these directories is an original sample. Tests may frame a copy in memory (one leading dot on lines that already begin with `.`, then a terminating `.\r\n`). They do not rewrite the files on disk. Expected canonical bytes are computed by the production reader, parser, canonical materializer, and yEnc validator, not stored as a second copy of the article.

## INN articles

- Upstream repository: `https://github.com/InterNetNews/inn`
- Upstream path: `tests/data/articles`
- Pinned commit: `6843efceb8a15774d23e4e2892d08daf54574314`
- Commit date: 2026-09-11T22:05:39Z (`Yet another bunch of grammar fixes in POD`)
- Imported: 2026-10-15, byte for byte from the raw blobs at that commit
- License: ISC-style grant in [articles/UPSTREAM-LICENSE.txt](articles/UPSTREAM-LICENSE.txt). The copyright and permission notice must remain with every copy. These samples are not in INN's per-file license exception list.

The upstream directory at that commit contains 25 files. All 25 were imported under their upstream names. Nothing from the rest of the INN tree was copied. No file was excluded.

| Files | Role |
| --- | --- |
| `1`, `2`, `3`, `xref` | Ordinary header and body samples, LF line endings |
| `4`, `5` | Unusual but structured header text, including a long header and a bare CR |
| `6` | Long LF body whose dot-leading lines are runs of `.` characters, not `.\r\n` |
| `7` | LF article with body lines `.`, `..`, and `...`, and a header line that itself begins with `.` |
| `wire-7` | INN wire form of `7`: CRLF, one extra dot on lines that begin with `.` (headers included), terminated by `.\r\n` |
| `wire-no-body`, `wire-strange`, `wire-truncated` | Incomplete wire bytes with no article terminator |
| `bad-empty`, `bad-hdr-*`, `bad-long-*`, `bad-msgid`, `bad-no-body`, `bad-no-header`, `bad-nul-body`, `bad-nul-header`, `bad-subj` | Upstream negative samples: empty, malformed headers, missing separator, NUL |

`InnArticleFixtureTests` reads every file in `articles/` except `UPSTREAM-LICENSE.txt`.

- Logical files are framed in the test and passed to `NntpStreamReader.ReadArticlePayloadAsync`. The destuffed bytes must equal the file.
- The same logical bytes go through `ArticleBodyWireStuffer.StuffBody`. Headers stay unchanged. Each body line that begins with `.` gains one dot. The NNTP terminator is not appended.
- `1`, `2`, `3`, `6`, `7`, and `xref` also go through `NntpArticleParser` and `NntpArticleCanonicalMaterializer`. The retained body matches the stuffer output.
- `wire-7` is already framed. The reader must return native `7` with bare LF converted to CRLF only. That conversion is the INN native-to-wire line-ending step; the reader does not perform it in the other direction, so the expected bytes are the CRLF form of `7`, not the LF file itself.
- `wire-no-body`, `wire-strange`, and `wire-truncated` must surface `EndOfStreamException` because the terminator never arrives.
- `bad-no-body` is rejected with `MissingHeaderBodySeparator`.
- `bad-no-header` begins with a single LF, which the parser treats as the end of an empty header section, then rejects with `MissingMessageId`.
- `bad-nul-header` is rejected with `ContainsIllegalControlByte`. Header NUL is an illegal header control byte. `ContainsNul` is not produced for these samples.
- `bad-nul-body` is accepted. A NUL in the body is not a parse failure.

None of the INN samples contain `=ybegin`, `=ypart`, or `=yend`.

## SABCTools yEnc

The `.yenc` files are NNTP responses: a status line, the article, and a terminating `.\r\n`, except `test_empty_file.yenc`, which is a 6-byte UTF-16 fragment and not an article. `SabctoolsYEncArticleValidationTests` strips the status line, destuffs with `NntpStreamReader`, and validates `NntpArticleParser` body bytes with `YEncArticleValidator`.

| Files | Expected validator status |
| --- | --- |
| `test_regular.yenc`, `test_regular_2.yenc`, `test_special_chars.yenc`, `test_special_utf8_chars.yenc`, `test_bad_crc.yenc` | `ValidMultiPart`. `test_bad_crc.yenc` declares `pcrc32` and that value matches the decoded part, so the validator accepts it. The upstream sabctools test compares two decoders and does not expect a CRC failure for this file. |
| `test_bad_crc_end.yenc` | `CrcMismatch` (`pcrc32=084`) |
| `test_partial.yenc` | `DecodedSizeMismatch`. The trailer declares `size=384000` and the encoded part is much shorter. |
| `test_invalid_crc_chars.yenc`, `test_malformed_ybegin.yenc` | `InvalidMetadata` |
| `test_invalid_escape.yenc` | `InvalidEscapeSequence` |
| `test_missing_yend.yenc` | `Truncated` |
| `test_ypart_without_ybegin.yenc` | `ValidNonYEnc` (no `=ybegin` line, so no section is opened) |
| `test_empty_file.yenc` | `ValidNonYEnc` on the raw bytes; the reader is not used |

`test_regular.yenc` is also validated before destuffing. The still-stuffed payload must not be accepted. Several of these files have body lines that begin with `.`; stuffing and yEnc are separate layers, and the validator sees the destuffed body.

## Adding a fixture

Copy the upstream bytes without transcoding. `.gitattributes` marks `tests/fixtures/articles/**` and `tests/fixtures/SabctoolsYEnc/**` as `-text` so `core.autocrlf` does not rewrite LF to CRLF on checkout. After adding a file, record its source, commit, and which test reads it in this README. Do not edit a sample to make a test pass. Add a clearly named synthetic file only when the real corpus has no example of the required negative case.

## Running the fixture tests

```powershell
dotnet test tests/BackFiller.tests/BackFiller.Tests.csproj --configuration Release -p:Platform=x64 --filter "FullyQualifiedName~InnArticleFixtureTests|FullyQualifiedName~SabctoolsYEncArticleValidationTests"
```

`ArticleCorpusBench.Measure_local_article_corpus` measures `.artifacts/articles` when that directory is present and writes `.artifacts/article-corpus-bench-2026-10-15.txt`. It does not modify the corpus. When the directory is absent, the fact reports that and returns; the corpus is local evidence and is not part of the git checkout.
