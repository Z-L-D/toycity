# Working on ToyCity

## Every release (each beta, point release or feature PR that changes what the game looks like)

1. Take test shots: the Godot gate run's contact sheet from CI (decode it from the job log) and web client shots
   taken with Playwright against a local server.
2. Save the best of them, as JPEGs about 800–1200 px wide, under `docs/screenshots/<release>/`.
3. Point the README's **Screenshots** section at the new set (keep older sets in their folders).
4. Add the release to the README's **Releases** table with its date, PR and gate report, and update the
   **Roadmap** table's states.
