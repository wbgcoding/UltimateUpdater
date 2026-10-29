# Security policy

Ultimate Updater starts installers with administrator rights, so security reports get priority over
everything else.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting instead
(the repository's **Security** tab, then **Report a vulnerability**, or straight to
https://github.com/wbgcoding/UltimateUpdater/security/advisories/new) and include:

- the app version (Settings, About) and whether it is the portable build or the installer,
- what an attacker needs (local user, another process, network position) and what they gain,
- steps or a proof of concept, and the relevant log lines with anything private removed.

You get an answer within a week. Once a fix is released, the report is credited in the changelog
unless you prefer otherwise.

## Supported versions

Only the latest release gets security fixes. The app's own update check offers it.

## Scope

In scope: the app, its elevated helper, the installer and the self-update. Out of scope: the
packages and installers delivered by third-party sources (winget, Windows Update, package managers,
launchers); report those to their owners.
