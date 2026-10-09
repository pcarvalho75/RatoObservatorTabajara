# Automatic updates

Mouse Tracker Tabajara checks the public [RatoObservatorTabajara releases](https://github.com/pcarvalho75/RatoObservatorTabajara/releases) for newer Windows x64 packages. Open **Updates** in the sidebar to check manually or change the automatic-check and prerelease options.

Automatic checking runs after interactive startup and periodically while the app stays open. Checking does not interrupt video processing, install anything, or restart the application. It requests public release metadata; it does not send recordings, mouse identities, study paths or results.

## Install a newer version

1. Open **Updates** when a new version is available.
2. Choose the download/prepare action. The updater downloads the complete package, checks its byte count and GitHub SHA256 digest, and verifies its extracted file manifest and version identity.
3. Finish or cancel any processing and close other report/review windows.
4. Choose the restart action. The app commits valid pending trial-table edits, retains a pristine **before-update** checkpoint and creates a separate **update-working** copy. The new version opens the working copy and confirms its process, version, project and blind-review state before the old window closes.

The updater installs each application version into its own folder under your local application data. It does not overwrite the running executable, older versions, your active study or the original recordings. Failed and canceled preparation remains separate from ready versions. A download cannot become runnable through a partial ZIP or a failed integrity check.

After a successful switch, use **Rato Observator Tabajara** in the Windows Start Menu. The updater creates that per-user shortcut for the installed version and its working project. If shortcut creation is unavailable, the app explains how to launch the installed version manually. Keep the original downloaded version for rollback.

Keep using the original video files in their existing locations. Saved project snapshots contain processed data and metadata; they do not copy the recordings. The current decoder's plain-ASCII video-path requirement still applies.

## Recovery and rollback

If download, verification, project snapshotting or new-version startup fails, the current application remains available. The failure is reported in the update window. Keep the current application folder and the retained project snapshots.

To return to an earlier version, launch its executable against a separate copy of the pre-update project. A project opened or modified by a newer version should not be handed to an older version without a verified schema-compatibility check. Automatic updating does not establish compatibility with arbitrary future project formats.

## First installation

Version **0.1.0-dev.12** predates the updater. Download an updater-equipped release once, extract the complete ZIP, and launch that version. Future releases can then be checked and prepared from the app.

The pilot releases are marked prerelease on GitHub. Keep prerelease checking enabled to receive newer pilot builds. Turning automatic checking off leaves manual checking available. Choosing stable releases only excludes the pilot prereleases.

## Integrity and scope

Updates are restricted to Windows packages in the configured `pcarvalho75/RatoObservatorTabajara` GitHub repository. The updater rejects unsupported versions, missing digests, incomplete assets, ZIP paths that escape the staging directory and mismatched file manifests. It does not run downloaded scripts or replace studies during package extraction.

An unreadable or malformed installed version identity holds update checks and installation while leaving the study interface usable. Open the trusted release page from **Updates** and extract a fresh package to repair the installation.

Release hashes protect downloaded-byte integrity relative to GitHub release metadata. These development packages remain unsigned. Updating does not turn synthetic checks into validation of real tracking accuracy or biological conclusions. Continue reviewing detection quality, animal identities, experimental allocation, endpoint eligibility and scientific interpretation locally.
