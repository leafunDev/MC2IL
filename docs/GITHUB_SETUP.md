# GitHub repository setup

This checklist is for when MC2IL is ready to publish. It does not change GitHub settings by itself.

## Protect the main branch

After the first push of the local main branch:

1. Open the repository's Settings → Rules → Rulesets.
2. Create a branch ruleset targeting the main branch.
3. Require a pull request before merging and require one approval.
4. Require the Build and test (.NET 10) status check after it has completed on GitHub at least once.
5. Require conversations to be resolved before merging.
6. Block force pushes and branch deletion.

The CODEOWNERS file requests a review from @leafunDev for every pull request. The branch rule makes the review requirement enforceable.

## Enable security features

1. Enable private vulnerability reporting in the repository's security settings before relying on the report link in `SECURITY.md`.
2. Keep the CodeQL workflow enabled so it can publish analysis results to the Security tab after the repository is published.
3. Review repository Actions permissions and keep workflow token permissions limited to what each job needs.

## Review a contribution

1. Open the pull request and read its description, changed files, and CI result.
2. Leave review comments or request changes when the behavior, tests, or licensing need clarification.
3. Approve and merge only after the change is ready.
4. Add the contributor's GitHub handle and contribution to CONTRIBUTORS.md after the merge when you want a curated acknowledgement.

GitHub's Insights → Contributors graph is generated from qualifying commits on the default branch. Commit author emails must be linked to GitHub accounts. Use `CONTRIBUTORS.md` as the project-maintained acknowledgement list for accepted MC2IL contributions.
