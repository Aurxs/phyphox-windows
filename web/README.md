# Windows browser UI

The browser performs presentation and sends commands only. It does not access BLE, USB, audio, or cameras directly, and it does not create substitute measurement data.

## Build

```sh
npm ci
npm run build
npm test
```

`dist/` contains local static assets without CDN dependencies. Development: `npm run dev`; Vite proxies `/api` to `http://127.0.0.1:37651`.

## Implemented

- Chinese/English shell; persisted dark/light theme; library search and `.phyphox`/ZIP upload.
- Session load, start, pause, stop, clear and grouped clear; state/CSV/XLSX downloads.
- Server snapshot listing/save/restore, explicit CSV/TSV/WAV import mappings, complete-journal replay in a separate read-only result view with numeric values and charts.
- Explicit audio enumeration, explicit camera probing/configuration, and user-requested existing-frame display; opening the media page never requests hardware.
- Real BLE experiment transfer download/import from a scanned selection; advanced output bindings and explicit one-shot output triggers, enabled only in appropriate server states.
- Dynamic values, information, separators, numeric edits, buttons, toggles, dropdown maps, sliders including two outputs, images fetched from the service.
- Basic conditional visibility, dynamic labels, value maps, edit factor conversion.
- Multiple graph datasets, official axis pairing rules, logarithmic/fixed/extend/follow axes, line/dot/official edge-based bar widths, RGB color stops and legends, spatially interpolated map grids / centered pixel cells, PNG export, observed-update history overlays, pan/zoom/fullscreen and target-specific picking/calibration values.
- Device capability status and honest unavailable/unsupported notices; explicit scan/select/connect, protocol JSON import/edit, raw frame inspection, manual hexadecimal writes, disconnect, and explicit input-binding configuration with source-replacement acknowledgement.

## Not yet complete

- Full official graph parity remains unverified. History captures browser-received changed datasets (at most 32), not every server analysis cycle; identical data updates and hidden-tab history are not retained. Pause-time overlays and precise large-data decimation/gap parity remain pending; source time mappings are typed but not drawn. Map interpolation is CPU rasterized with a 1024-entry color lookup and is not claimed pixel-identical to Android OpenGL.
- Full range-slider crossing rules, image theme filters, all formatting and localization tokens.
- Device workflows require functioning service adapters and real hardware verification; explicit JSON protocol-to-experiment binding is available but requires real hardware acceptance. Camera/depth views remain visibly unsupported.
- WebSocket deltas; currently sequential polling at 150 ms while running and 1 s otherwise.
- Actual Windows browser/device acceptance and comprehensive visual fixture comparison.

These limitations must remain in the product compatibility matrix; a successful web build is not a claim of full experiment or hardware compatibility.

## Verification performed

TypeScript check and Vite production build passed on the development macOS host. Five minimal tests derived from official graph pairing and calibration value-map behavior passed. A real local browser loaded the library, changed the formula sample from 2→4 to 3→9, exercised start/pause/stop and downloaded an export. Device capability/empty-state pages and hardware-experiment Start refusal were checked. Dark Chinese and light English screenshots were inspected in `output/playwright/`. A later browser check also passed server snapshot save/list/restore, separate replay values, CSV import with 5→25 and source labels, and restoration to 2→4. Media-page entry emitted zero media requests; explicit audio enumeration returned expected unsupported-platform HTTP 422 on macOS. Missing-device BLE download remained disabled and unbound output triggers were absent. This macOS development-host verification does not replace Windows or device acceptance.
