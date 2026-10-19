# PortalVisionFix

### A small patch to fix unscheduled `PortalVisionJob` in ULTRAKILL's portals.

TLDR: it makes enemies see thru recursive portals properly (e.g. 8-3's Red Path Clockwork Rotator arena or Sliding arena)

Vanilla PortalScene builds every sequence up to 2 portals (1 + N + N^2) and:
1. never schedules PortalVisionJob, so visionPossible stays all-true;
2. marks a sequence culled when vision IS possible (missing '!');
3. aliases every depth-2 sequence sharing a first portal to one PortalHandle[], so they all read [first, lastPortal] while their sequenceMatrices are correct;
4. only reads culledSequences from Vision.GetDataRef, which nothing calls.

This plugin runs the vision job and rebuilds sequences as a pruned tree up to MaxPortalDepth: a chain is extended with portal j only if the side it exits from can geometrically see j. The live vision path (TargetTracker jobs, Vision.TrySee) only maps candidate index -> sequence index (index / targetCount), so it accepts any sequence list with matching matrices.