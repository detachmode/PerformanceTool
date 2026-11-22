# Shutdown program and runtime data in the server runner (brain 0052, step S2)

Live mode (server) unless noted. Preflight `check-readiness.cs` writes the run bag, the scenarios write it too, the shutdown program cleans up.

1. Plan editor: the shutdown program row under the preflight row.
![Shutdown row](01-editor-shutdown-row.png)

2. The picker for the shutdown program (same look as the preflight picker).
![Shutdown picker](02-editor-shutdown-picker.png)

3. Environment tab during the live run: run bag filled by the preflight and the bees.
![Live run](03-environment-live-run.png)

4. The run is over, the shutdown program runs: the player bar says "Shutting down", the strip shows its output.
![Shutting down](04-player-bar-shutting-down.png)

5. Environment tab while the shutdown program runs.
![Environment during shutdown](05-environment-during-shutdown.png)

6. After the run: final values, including what the shutdown program wrote (cleanedUp, deletedUsers, tenantId).
![Final values](06-environment-final-values.png)

7. Drill-down: the writer "Shutdown" opens the program's output.
![Writer drill-down](07-writer-shutdown-drilldown.png)

8. Overview after the run: one quiet line, output on demand.
![Finished](08-overview-shutdown-finished.png)

9. The same with the output opened ("Logs").
![Logs](09-overview-shutdown-logs.png)

10. A stopped run: the shutdown program still runs.
![Stopped, shutting down](10-stopped-run-shutting-down.png)

11. The stopped run after the shutdown program finished.
![Stopped, done](11-stopped-run-shutdown-done.png)

12. A failing shutdown program: shown as failed with its error and logs, scenario results unchanged.
![Failed shutdown](12-shutdown-failed.png)

13. Script editor: shutdown programs next to scenarios and preflights (END tag).
![Script editor](13-script-editor-shutdown.png)

Fake mode:

14. Overview with the fake shutdown program output.
![Fake overview](14-fake-overview-shutdown.png)

15. Environment tab: values written by "Shutdown".
![Fake environment](15-fake-environment-shutdown-values.png)

16. Writer drill-down in Fake mode.
![Fake drill-down](16-fake-writer-drilldown.png)
