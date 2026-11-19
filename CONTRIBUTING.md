# Contributing to MC2IL

Thanks for helping improve MC2IL. Focus changes on a clear bug or capability and include enough context to reproduce the result.

All project discussions and pull requests follow the [Code of Conduct](CODE_OF_CONDUCT.md). For usage questions or help preparing a report, see [SUPPORT.md](SUPPORT.md).

## Set up

1. Install the .NET 10 SDK.
2. Restore and build from the repository root:

       dotnet restore MC2IL.slnx
       dotnet build MC2IL.slnx --configuration Release --no-restore

3. Run the relevant tests before opening a pull request:

       dotnet test MC2IL.slnx --configuration Release

The [architecture guide](docs/ARCHITECTURE.md) maps the solution projects and processing flow.

## Contribution flow

1. Fork MC2IL, create a focused branch, and push your changes to your fork.
2. Open a pull request targeting the main branch. The MC2IL CI workflow builds the solution and runs its tests.
3. The maintainer reviews the proposal, may request changes, and decides whether to merge it.
4. After an accepted pull request is merged, GitHub can attribute qualifying commits to the contributor. The commit author email must be linked to their GitHub account, and the commits must be present on the default branch.
5. The maintainer may also add the contributor to CONTRIBUTORS.md after the accepted work is merged.

GitHub's Insights → Contributors graph shows up to 100 qualifying commit contributors and can take time to refresh. `CONTRIBUTORS.md` is the maintainer-curated list of people whose accepted work is recognized for MC2IL.

The owner can make review and CI requirements mandatory with a branch ruleset. See [GitHub repository setup](docs/GITHUB_SETUP.md); those settings must be enabled on GitHub after the repository is published.

## Reports and pull requests

- For a bug, include the MC2IL version, operating system, Unity version, target architecture, output format, and a short log excerpt.
- Never attach a game APK, proprietary binary, or metadata dump unless you have the right to share it. Describe how to reproduce the issue with a lawful sample.
- Keep pull requests focused. Explain the observed behavior, the expected behavior, and how you verified the change.
- Preserve existing copyright and license notices, and include license information when adding third-party code or assets.
- Update documentation when a command-line option, output format, or supported input changes.
- Use a clear branch name such as feature/arm64-literals or fix/apk-detection.

## Project direction

For changes to public namespaces, plugin contracts, or emitted attributes, explain the compatibility impact clearly in the pull request.
