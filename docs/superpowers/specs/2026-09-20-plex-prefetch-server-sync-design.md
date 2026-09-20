# Plex Prefetch Server Synchronization Design

## Problem

The Plex settings card and Smart Prefetch card are sibling React components with independent server state. Saving Plex servers updates only the Plex settings card. Smart Prefetch therefore continues showing its mount-time server snapshot until the page reloads.

## Design

Add a small typed client-side notification API beside the existing Plex bootstrap API. After a successful Plex server save, the Plex settings card will publish the exact server list returned by the backend. Smart Prefetch will subscribe while mounted and replace its local server list with that payload.

The notification will also be published after refreshes that can change server availability, such as disconnecting a Plex account. Smart Prefetch will clear a selected source server if that server no longer exists in the new list. Initial page loading remains unchanged and continues to use the backend bootstrap response.

This is deliberately local to the browser page. It adds no persisted setting, backend endpoint, polling loop, or extra network request.

## Error Handling

Notifications are emitted only after a successful backend response. Existing save and refresh errors remain visible in the Plex settings card and do not replace Smart Prefetch's last known list.

## Testing

Add a focused component regression test that starts Smart Prefetch with no Plex servers, publishes the server list produced by a successful save, and verifies the new server becomes selectable without remounting or reloading the page. Existing Plex and Smart Prefetch component tests must remain green.
