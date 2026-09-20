# Plex and Smart Prefetch Setup Design

## Goal

Make a discovered Plex server impossible to mistake for a saved server, and keep the normal Smart Prefetch setup as simple as the approved mockups.

## Root cause

Plex discovery currently renders candidate servers with a separate **Choose** action. Candidates do not enter the `servers` state until that action is clicked, while **Save Plex servers** stays enabled even when `servers` is empty. Saving immediately after discovery therefore sends an empty list, receives HTTP 200, and reports success without saving a detected server.

Production already contains the four essential Smart Prefetch policy controls, but the full Plex source catalogue and queue are still expanded below them. The page therefore remains visually complex even though policy tuning moved under **Advanced settings**.

## Plex server setup

- Render discovered servers as large single-selection cards. Selecting a card is the explicit choice; remove the intermediate **Choose** action.
- Keep the advertised connection selector inside its server card.
- Present **Save selected server** as the only primary action for discovery. Disable it until a candidate with a connection is selected.
- Saving merges the selected candidate into the existing saved-server list by machine ID, preserving every unrelated saved server and its path mappings.
- A successful save replaces local state with the authoritative backend response, publishes it to Smart Prefetch, and reports `<name> saved. It is ready for Smart Prefetch.`
- Discovery refresh is a quiet secondary action. It never changes persisted configuration.
- Existing saved/manual server editing, path mappings, testing, removal, and bulk save remain available under a collapsed **Advanced server configuration** disclosure.
- The advanced save action is unavailable when no server exists, preventing another successful empty-list save.
- Backend validation and credential ownership remain unchanged.

## Smart Prefetch setup

- The visible policy card contains exactly **Enable Smart Prefetch**, **Movies**, **TV episodes**, and **Daily download budget (GB/day)**.
- Keep the smart-defaults summary and collapsed **Advanced settings** policy disclosure.
- Show a compact Plex connection status: the server name for one enabled server, a count for multiple enabled servers, or a clear not-connected message.
- Put Plex libraries/users/sources under a collapsed **Plex libraries and sources** disclosure.
- Put operational queue controls under a collapsed **Prefetch activity** disclosure.
- Keep the page-level Settings save action authoritative for Smart Prefetch policy and source changes.

## Error handling

- Discovery and save failures continue through the existing alert surface.
- A candidate cannot be saved without a selected advertised endpoint.
- A failed save leaves the prior saved-server state intact and does not publish candidate state to Smart Prefetch.
- Environment-managed Plex settings remain read-only.

## Verification

- Component tests must prove discovery alone cannot submit an empty save, selecting and saving sends the candidate handle, the success message is specific, and the saved server reaches Smart Prefetch immediately.
- Component tests must prove only the four essential policy controls and Plex status are visible by default, while source and queue controls become visible only when their disclosures are opened.
- Live verification must prove a selected production candidate persists across reload and appears in Smart Prefetch without restarting the container.

