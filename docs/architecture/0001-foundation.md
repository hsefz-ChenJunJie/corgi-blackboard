# ADR 0001: Workspace, Surface Ownership, and Coordinates

Status: Accepted for phase one

## Context

The workspace combines an infinite blackboard, imported documents, page annotations, screen-fixed window layout, and reading position. Those concerns must remain independently addressable: a viewport or window change cannot rewrite teaching content, and a document's changing page order cannot rebind committed annotations. Core is a plain `net10.0` library and cannot depend on WPF, Win32, filesystem, logging, timer, or UI types.

The phase-one behavior in SPEC sections 2-9 includes image documents now and PDF/PPT pages, history, persistence, selection, screenshots, desktop annotation, constrained geometry, coordinate systems, an independent whiteboard, and optional recognition later. This record defines extension boundaries without implementing candidate features or empty services.

## Decisions

- `WorkspaceId`, `SurfaceId`, `DocumentId`, `AssetId`, `ElementId`, and `ContentOperationId` are distinct value types. A workspace owns surfaces, documents, imported asset identities, and elements. A stroke belongs to exactly one surface; a document window is layout state, not a content element.
- The blackboard is a `Board` surface with its own translating camera. An image document references one asset and one page surface. The document stores the ordered page-surface identity list even though the image adapter currently creates exactly one page. Reading state points at a stable surface ID. Existing page annotations therefore remain attached if a future multipage adapter changes ordering.
- An abstract `Element` carries immutable identity and local transform in one ordered, surface-owned store. `StrokeElement`, the only production subtype, adds an immutable centerline, fixed color and width, and optional source element identity. Eraser fragments use fresh element IDs and retain their source ID. Both eraser paths select only strokes before geometry. A non-stroke subtype exists only in Core boundary tests; no candidate feature is implemented. The image asset is separate from page elements and is never a target of content operations.
- Logical units are DIPs. `BoardPoint`, `ScreenPoint`, and `PagePoint` keep coordinate domains explicit; all conversions are through a finite, positive-scale `ViewportTransform`. Board camera motion translates only. Image page coordinates are original image pixels; normal and filled window modes read the same surface content. WPF converts physical pixels to application DIPs at its platform boundary.
- Window rectangles, z-order, minimized/fill state, reading positions, and current page live in separately revisioned layout and reading state. They are not content history. A layout or camera action cannot create `ContentOperation`.
- A pending `ContentOperation` captures its immutable before/after `Element` arrays and the start surface. `Workspace.BeginElement` applies the shared ownership/identity/transform checks; `BeginStroke` delegates to it. It mutates workspace content only on commit; cancel discards it. Page editing must lock the page surface at gesture start, not consult a later current-page index. Validation occurs before revision changes.
- The future save DTO is versioned independently from runtime types. It will carry `version` and `minReaderVersion`, workspace identity, asset identities and payload references, ordered document page-surface IDs, elements grouped by owning surface, and separate layout and reading-state values. No archive format or persistence service is implemented in this phase.

Example shape only; this is not a shipped serializer contract:

```json
{
  "version": 1,
  "minReaderVersion": 1,
  "workspaceId": "stable-workspace-id",
  "assets": [{ "assetId": "stable-asset-id", "contentRef": "assets/stable-asset-id" }],
  "documents": [{
    "documentId": "stable-document-id",
    "pageSurfaceIds": ["stable-page-surface-id"]
  }],
  "surfaces": [{
    "surfaceId": "stable-page-surface-id",
    "elements": [{ "elementId": "stable-element-id", "kind": "stroke" }]
  }],
  "layout": [{ "documentId": "stable-document-id", "x": 20, "y": 30 }],
  "reading": [{ "surfaceId": "stable-page-surface-id", "offsetX": 0, "offsetY": 240 }]
}
```

## Extension map

The phase-one audit with actual types, future seams, and per-capability walkthroughs is in [spec-extension-map.md](spec-extension-map.md).

| SPEC area | Existing foundation and future connection | Invariant |
| --- | --- | --- |
| Sections 3-4: image, PDF/PPT pages and annotations | `ImageDocument`, page `Surface`, immutable strokes, and explicit viewport. Future document adapters produce ordered page surfaces and static page assets. | Background source is immutable; annotations belong to page identity and one operation stays on its start page. A conversion failure cannot mutate the workspace. |
| Sections 4.3-4.4: clear and global history | `ContentOperation` is the atomic surface delta input to a future workspace command/history layer. | One user action is one history unit. Window layout, reading, and camera changes never enter content history. |
| Section 5: save, migration, recovery | Stable workspace/asset/surface/element IDs and separate layout/reading revisions are the DTO inputs to a future archive boundary. | Archives own imported source data and static pages, not external paths. A saved snapshot covers content, layout, and reading state consistently. History need not persist. |
| Section 6: selection, transforms, copy | Surface-owned elements, stable IDs, local transforms, and source identity provide future hit-test and remapping data. | Selection resolves one owner surface; a copied coordinate system remaps internal IDs instead of depending on the original. |
| Section 7: software screenshots | Future composition capture consumes content render data separately from `ImageWindowLayout`. | Capture excludes window chrome and stores the resulting image as a normal element on the resolved target surface. |
| Section 8: desktop annotation and screenshots | A future App lifecycle/platform adapter creates a separate overlay session and its own screen-origin transform. | Desktop coordinates do not reuse application-client `ScreenPoint`; desktop strokes stay outside workspace save and history. |
| Section 9.1: constrained shapes | Future element kinds extend the surface element collection with semantic geometry. | Constrained shapes retain their geometric constraints; they are not flattened to arbitrary strokes. |
| Section 9.2: coordinate systems | A future composite element can own typed child objects and a dependency graph. | Mathematical units remain equal and axes perpendicular; resizing relayouts rather than nonuniformly scaling the coordinate system. |
| Section 9.3: independent whiteboard | A future `SurfaceKind` can add a separate board and camera. | It is not a document or nested window; document pages do not recursively contain surfaces. |
| Section 9.4: offline recognition | A future opt-in adapter can inspect a pending operation before commit and propose a replacement. | Disabled by default, offline, nonblocking, and represented as reversible content rather than a hidden mutation. |

## Consequences

Core contracts can be exercised without a desktop runtime. The application layer is responsible for mapping OS input and physical pixels into application DIPs; infrastructure owns decoding and future archive IO. No persistence, history service, input arbiter, renderer, or recognition service is introduced by this decision.
