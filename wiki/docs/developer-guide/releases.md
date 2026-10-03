# Release process

The GitHub release tag is the **single source of truth** for published extension versions.
Do not manually edit the project, assembly, manifest, package filename, or other internal
version fields just to make a release.

## Publish a release

1. Merge the reviewed changes to `main` and wait for normal CI to pass.
2. Open **Releases → Draft a new release**.
3. Create a semantic version tag such as `v0.2.0` on the commit you want to release.
4. Publish the release. GitHub Actions validates the tag, builds with version `0.2.0`,
   stamps `extension.yaml`, generates the matching assembly/file version, packages the
   PEXT, validates its contents, and uploads `UnnamedTrackingPlaynite-0.2.0.pext` to
   that release.
5. Install the exact attached PEXT and perform the Windows/Playnite runtime acceptance
   checks before treating the release as verified.

GitHub's `release.published` event runs against the tagged release commit, so the
published binary is built from the exact commit selected for the release. citeturn2search5

## What you no longer need to do

- Do not edit `AssemblyVersion` or `FileVersion`.
- Do not edit `extension.yaml` for every release.
- Do not rename a PEXT manually.
- Do not make a version-only commit before every release.

Local development defaults to the last stable source version so ordinary builds continue
to have a valid Playnite manifest. Release CI overrides that value from the tag.

## Tag rules

Stable release tags must be exactly `vMAJOR.MINOR.PATCH`. Tags such as `0.2.0`,
`v0.2`, or `v0.2.0-beta` are rejected by the stable-release workflow. If prereleases
are needed later, add an explicit prerelease workflow rather than silently changing the
stable-version rules.

Playnite requires a valid `extension.yaml` manifest and distributed extensions are
normally packaged as `.pext` files. citeturn0search0
