# 0028. Failed and slow actors first: colour, order and retention

- Status: Accepted (user decisions)
- Date: 2026-11-20

## What
Failed or slow actors are usually more interesting to the user than successful ones. So, across the whole UI:
- **Actor numbers carry the outcome colour** (`#192` in red / yellow / green / blue for running) wherever they are
  shown: logs, popup lists, labels, chips.
- **General ordering rule for actors:** **failed → slow → successful**, then by time. This applies wherever actors are
  listed or grouped (e.g. the actors of a time bucket, actor lists, log filtering by actor). Scenario cards keep their
  plan order so they don't jump around.
- **Retention priority:** when the limit of kept detail is reached (calls/logs, currently 1500 actors per scenario,
  configurable), **successful actors are dropped first**, then slow, then failed. Failed and slow actors are kept longer.
- **Error dictionary:** store each distinct error (message, exception text) once and reference it from actors/logs,
  to save memory and, later, disk space.

## Why
Users investigate problems. The UI and the storage should spend their attention and space on the actors that went
wrong, not on the many that went fine.
