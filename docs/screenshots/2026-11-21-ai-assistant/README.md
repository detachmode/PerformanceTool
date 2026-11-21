# AI assistant (brain 0047 to 0051)

Live mode against a real run (a small local system under test), the assistant answering from a local OpenAI-compatible stub with scripted tool calls.
Device scale factor 2.

## Chat icon and panel
Fake mode has no chat icon (the GitHub Pages demo has no server):

![Fake mode, no chat icon](09-fake-mode-no-chat-icon.png)

Live mode shows the icon when the server reports the assistant as configured, and the panel pushes the page aside:

![Live mode with the chat icon](01-live-overview-chat-icon.png)
![Panel open, page pushed aside](02-panel-open-pushes-page.png)

## Activity chips and cards
Reads fold into "1 action"; a click reveals what was sent and returned. The run card has clickable figures (bees, errors, slow, per scenario entry):

![Run card and a revealed chip](03-run-card-and-chip-revealed.png)

A logs card: a few lines, the total, and Show more:

![Logs card in the chat](04-logs-card-in-chat.png)

Show more opens the existing Logs popup, pre-filtered (scenario entry, failed bees):

![Logs popup from the card](05-logs-card-show-more-logs-popup.png)

## Bee timeline
Hand-picked bees from two scenario entries on one time line, with the agent's short reason per bee:

![Timeline card in the chat](06-timeline-card-in-chat.png)

Show more opens the popup chart for exactly these bees: click a bar, calls and logs, zoom, step through failed and slow:

![Timeline full screen, bee selected](07-timeline-full-screen-bee-selected.png)
![Timeline full screen, waterfall](08-timeline-full-screen-waterfall.png)

## The agent edits the plan, the editor follows
The editor before, and after the agent changed the rate of Checkout (no reload; the "Changed" marks and 150 bees/min come from the server-owned plan):

![Editor before](10-editor-before-agent-edit.png)
![Editor after the agent's edit, with a plan changes card](11-editor-updated-by-agent.png)

## The agent starts a run directly (no approval, brain 0048)
The activity chip is the record; the run is going in the player bar:

![Agent starts a run](12-agent-starts-run-directly.png)

## Mobile
A full-screen sheet below the top bar:

![Mobile sheet](13-mobile-sheet.png)

## Published (Release, trimmed) client
The same timeline flow against the trimmed Release publish of the client, no console errors:

![Published build](14-published-trimmed-build-timeline.png)
