# Glossary

Domain vocabulary for the YARG fork's custom features. Terms only; no implementation detail.

- **Section**: a named span of a chart (Chorus 1, Verse 2) from the chart's section markers. Charts with no markers get ten auto-generated percentage buckets that are treated as sections.
- **Section FC**: a section in which the player hit every note (full combo) on the surviving timeline of the current run.
- **Star Power path**: the predicted set of Star Power activations that maximises score from the player's current state.
- **Rewind target**: a section the player has already entered in the current run and may rewind to. Sections ahead of the player are never targets.
- **Section rewind**: the player's action of returning song time to a target section's start, with their state restored to what it was when they first entered that section on the surviving timeline.
- **Surviving timeline**: the run as it stands after rewinds. A section rewind discards everything after the target; later sections are played and recorded afresh.
- **Lead-in**: the short interval before a rewind target's first notes during which audio and highway already play so the player can settle in.
- **Rewound run**: a completed run in which at least one section rewind occurred. It scores normally and is marked with a badge on the score page.
