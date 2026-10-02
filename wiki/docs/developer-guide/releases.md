# Release process

Version **0.1.2** is prepared as an unreleased update. This change does not publish
a release. Runtime changes require consistent metadata before a future release:
project `Version`, `AssemblyVersion`, `FileVersion`, extension manifest, package
filename, README and release notes. The validators compare the project, built
assembly/file version, packaged manifest and PEXT filename.

1. Review/merge the companion PR against `main` after both build workflows and
   the wiki job pass. Keep unrelated work such as the AFK PR separate.
2. Complete the Windows/Playnite [runtime acceptance checklist](../user-guide/screenshots.md)
   and capture the actual requested screenshots. Record exact versions/results.
3. Verify release notes and all version metadata. Build the PEXT from a clean
   checkout and validate its actual entries. Inspect the resulting artifact.
4. When release publication is explicitly intended, create and push the matching
   `v0.1.2` tag on the reviewed commit. The PEXT workflow refuses a mismatched tag.
5. Review the generated release notes and attachment, then test installation of
   that exact PEXT. Do not rebuild and silently replace an already distributed
   version with different content.

Main pushes and draft PRs build artifacts only. The existing release action runs
only for an intentional version tag; ordinary maintenance commits do not create
a release. No force-push, unrelated branch edits, or automatic PR merge is needed.
