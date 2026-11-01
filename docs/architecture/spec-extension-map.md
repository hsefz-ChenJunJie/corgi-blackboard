# Full-SPEC extension map

Status: phase-one audit based on `19d57f47fe6394662262399d688c03d0eaacc0d5`, including the task12 shared-element boundary repair after `f00363af2ab3293d7c5630504a0f2420c665cb58`.

This map traces each later SPEC capability to the types that exist today and to the place where future code will connect. Names marked **(future)** don't exist in the repository. They describe a planned seam, not a shipped service, and phase one creates no empty or null-returning stand-ins for them.

All "actual" names below were read from source under `src/CorgiBlackboard.Core/`, `src/CorgiBlackboard.Infrastructure/`, and `src/CorgiBlackboard.App/`.

## Current foundation

| Concern | Actual types and members | Owner |
| --- | --- | --- |
| Identity | `WorkspaceId`, `SurfaceId`, `DocumentId`, `AssetId`, `ElementId`, `ContentOperationId` (Guid record structs, `Core/Model/Identifiers.cs`) | Core |
| Surfaces | `Surface(Id, Kind, ElementIds)`, `SurfaceKind { Board, DocumentPage }`, `Workspace.BoardSurfaceId`, `Workspace.Surfaces` | `Workspace` |
| Documents and assets | `Asset(Id, MediaType, Length)`, `ImageDocument(Id, AssetId, PixelWidth, PixelHeight, PageSurfaceIds)` with exactly one page, `Workspace.AddImageDocument`, `Workspace.CloseDocument`, `Workspace.ResourceRevision` | `Workspace` |
| Content | Abstract `Element(Id, Transform)` and concrete `StrokeElement(Points, Style, SourceElementId)`, `ElementTransform`, `StrokeStyle`, `Workspace.GetElements`, `Workspace.GetDocumentElements`, `Workspace.ContentRevision` | `Workspace`, per surface |
| Content change | `Workspace.BeginElement` and its typed `BeginStroke` entry return a pending `ContentOperation(Id, SurfaceId, Before, After)` with immutable `Element` arrays; `Commit` calls internal `Workspace.Apply`, `Cancel` discards | Core |
| Partial erase | `StrokeGeometry.Erase`, `EraseOperation.Begin(workspace, surfaceId, lockedPageSurfaceId, path, radius, ...)` wrapping one `ContentOperation` | Core |
| Coordinates | `BoardPoint`, `ScreenPoint`, `PagePoint`, `ViewportTransform` (`ToScreen`, `ToBoard`, `ToPage`) | Core |
| View state | `BoardCamera` (`Pan`, `Revision`), `ReadingState` (`CurrentPages`, `Offsets`, `Revision`), `WindowLayoutState` (`Open`, `Move`, `Resize`, `EnterFill`, `Close`, `Revision`, `Changed`), `ImageViewport` | `Workspace` |
| Input | `PointerSample`, `InputArbiter` (Idle, Editing, Navigating, AwaitAllReleased), `EditingSession` (`PendingChanged`, `ContentChanged`, `CameraChanged`, `ReadingChanged`) | Core |
| Rendering and windows | `CommittedInkLayer`, `TransientInkLayer`, `StrokeRenderer`, `ImageWindow`, `ImageWindowHost`, `ImageContent`, `WpfPointerAdapter` | App |
| Decoding and diagnostics | `ImageLoader`, `DiagnosticSession`, `DiagnosticLogManager`, `FeedbackExporter`; Core only defines the `DiagnosticEvent` record | Infrastructure |

## Cross-cutting invariants

**Content, reading, and layout stay apart.** Content lives in the per-surface element lists and moves `ContentRevision`. Reading position lives in `ReadingState` with its own `Revision`. Window rectangles, z-order, minimize, and fill live in `WindowLayoutState` with its own `Revision`. The board camera has its own `Revision`. `WindowLayoutState` and `BoardCamera` hold no reference to element lists, so a layout or camera action can't produce a `ContentOperation`. Only content is a candidate for the future history. All three are plain values that a future save DTO can copy: Guids, doubles, `WindowRectangle`, and `(X, Y)` tuples.

**Ownership is fixed at gesture start.** `BeginStroke` and `EraseOperation.Begin` both reject a document edit unless `lockedPageSurfaceId` equals the target page surface. A later page index or window move can't rebind committed content, because strokes store `PagePoint` values in page pixels and are keyed by `SurfaceId`.

**Plain, UI-free data.** Core targets plain `net10.0` and references no WPF, Win32, file system, logger, or timer. Elements are immutable records of Guids, doubles, and a `uint` color. WPF `Geometry` and render caches are built in App from those records and are never stored. No later capability needs to serialize a WPF control or rewrite stored point coordinates.

**Time and logging are injected.** Core reads no clock. Time enters as `PointerSample.Timestamp`, a monotonic value set by the App adapter. Core reports state changes through plain .NET events on `EditingSession`, `InputArbiter`, `WindowLayoutState`, and `ImageViewport`. App turns them into `DiagnosticEvent` records and hands them to `DiagnosticSession`, whose constructor accepts `utcNow` and `monotonicTicks` delegates. `DiagnosticLogManager` takes `utcNow` the same way. Tests can drive every Core path without a UI or a real clock.

**The eraser only sees strokes.** `Workspace` stores `Element` values in one ordered surface collection. Both `EraseOperation.Begin` and `EraseGesture` explicitly select `OfType<StrokeElement>()` before geometry, and `EraseOperation.Before/After` remain typed stroke arrays. `CommittedInkLayer` likewise selects strokes for ink drawing. `StrokeEraseTests.Erase_preserves_same_page_nonstroke_identity_and_data` inserts a test-only `Element` subtype through `BeginElement`, sweeps across its location and a stroke through both eraser paths, and checks its identity, payload, transform, ordered store membership, and the correct stroke fragments. `Asset` and `ImageDocument` remain separate resources. Shapes, pasted images, coordinate systems, their tools, and their renderers are future work.

**Known model limits, not breaks.** Later changes stay inside Core's model:

- The shared `Element` base supplies identity and local transform; `StrokeElement` remains the only production subtype. Future kinds supply their own typed data and behavior without replacing stroke coordinates or the shared store.
- `ContentOperation` covers one `SurfaceId`. A whole-document clear needs a multi-surface `IWorkspaceCommand` **(future)** that groups several surface deltas into one history entry.
- `Workspace.CloseDocument` drops page elements and the last asset reference without keeping undo data. The source comment says a future close command must keep its own copy. That's acceptable today because phase one has no undo UI.

## Row-by-row map

| SPEC | Actual foundation | Future connection | Invariant |
| --- | --- | --- | --- |
| §3-4 image, PDF/PPT, paging, continuous reading | `ImageDocument.PageSurfaceIds`, `SurfaceKind.DocumentPage`, `ReadingState.CurrentPages/Offsets`, `ImageViewport`, `ImageLoader` (Infrastructure) | `IDocumentPageSource` **(future)** yielding ordered page surfaces and static page assets; PDF renderer and offline converter in Infrastructure **(future)** | Background asset isn't content; annotations use `PagePoint` on a page `SurfaceId`; one gesture edits only its start page; a failed conversion adds nothing to `Workspace`; legacy PPT and PPTX are accepted separately |
| §4.3-4.4 clear, global history | `ContentOperation` with `Before`/`After`, `ContentRevision` | `IWorkspaceCommand` **(future)** grouping one or more surface deltas; history service **(future)** | One user action is one history entry; layout, reading, and camera never enter history; full-board or full-document clear differs from select-visible |
| §5 save, migration, recovery | Stable IDs, `Asset.MediaType/Length`, separate content/reading/layout revisions; DTO example in ADR 0001 | Versioned ZIP archive with `version`/`minReaderVersion` **(future)**; separate recovery slot **(future)** | Archive owns source and static pages, never external paths; atomic replace; migration validates before writing; snapshot covers content, layout, and reading together; history isn't persisted |
| §6 selection, transform, copy | `ElementTransform` on each element, `StrokeElement.Bounds`, `SourceElementId`, per-surface ownership | Selection, transform, and clipboard services **(future)** | Intersect or partial cover selects; cross-window selection resolves to board ownership per §6.2; internal paste stays editable and keeps size, fitting uniformly when needed |
| §7 software screenshots | Content layers (`CommittedInkLayer`, `TransientInkLayer`) are separate from window chrome (`ImageWindow`, `TeachingToolbar`) | Composition capture service **(future)**; image element kind **(future)** | Capture excludes chrome; smallest closed region wins, ties go to the first formed; transparent crop; result belongs to the current fill page or board |
| §8 desktop annotation and screenshots | App lifecycle in `App`/`MainWindow`; Core ink is UI-free and reusable | Platform adapter and `OverlaySession` **(future)** with its own desktop-origin transform | Entered from minimize, not normal switch; click-through; own history; nothing saved on exit; desktop points never reuse application `ScreenPoint` meaning |
| §9.1 constrained shapes | Element transform with separate `ScaleX`/`ScaleY` | Shape element kinds and tool adapters **(future)** | Circle, square, regular polygon, right triangle keep their constraints; strokes and images may scale non-uniformly; shapes aren't saved as plain paths |
| §9.2 coordinate systems | Surface-owned elements with stable IDs and local transforms | Composite element with typed children and a dependency DAG **(future)**; math input adapter **(future)** | Equal units, perpendicular axes; frame resize relayouts instead of distorting; dependents update and cascade delete asks first; copy remaps internal IDs |
| §9.3 independent whiteboard | `SurfaceKind`, the single `Workspace.Camera` (`BoardCamera`), `Workspace.BoardSurfaceId` | New `SurfaceKind` value and its own camera **(future)** | Not a document and not a nested window; shares tools, save, and global history; no recursive nesting |
| §9.4 offline recognition | Pending `ContentOperation` exposes `After` before `Commit` | Opt-in recognizer adapter **(future)** proposing a replacement operation | Off by default; offline; never blocks ink; result is a normal undoable operation; no model ships in phase one |

## Walkthroughs

Each walkthrough marks which steps run on today's code and which are future.

### PDF page two annotation and global undo

1. **(future)** `IDocumentPageSource` opens a PDF. Workspace registers one `Asset` for the file and a document whose `PageSurfaceIds` lists one `SurfaceId` per page, each a `SurfaceKind.DocumentPage`. Today `ImageDocument` enforces exactly one page, so a multipage document type is required.
2. The reader scrolls. `ReadingState.SetCurrentPage` and `SetOffset` move only `Reading.Revision`.
3. A pen lands on page two. The App adapter builds a `PointerSample` with page two's `SurfaceId` and its `ViewportTransform`. `EditingSession` calls `Workspace.BeginStroke(page2, page2, stroke)`. Points are `PagePoint` values in page units.
4. Release calls `ContentOperation.Commit`. `ContentRevision` moves once, and `EditingSession.ContentChanged(page2)` fires.
5. **(future)** The history service wraps that operation in an `IWorkspaceCommand`. Undo applies `Before` in place of `After` on page two, even if page one is now current. Inserting a page later doesn't rebind the stroke, because ownership is the page `SurfaceId`, not an index.

### Offline PPT conversion, archive, reopen on another machine

1. **(future)** An Infrastructure converter turns a legacy PPT or a PPTX into static page images offline. On failure it returns an error before any `Workspace` call, so nothing changes.
2. On success, Workspace registers the source file and each page image as `Asset` entries and creates one page surface per slide.
3. Annotations work as in the PDF flow.
4. **(future)** Save writes a versioned ZIP: manifest with `version` and `minReaderVersion`, source and page assets by `AssetId`, elements grouped by owning `SurfaceId`, and separate layout and reading sections. The write is atomic over the formal file.
5. **(future)** Another machine opens the archive with no converter installed. Pages come from the stored static images, so the document reopens with the same surface IDs and annotations.

### Cross-window selection, screenshot, paste

1. **(future)** The selection tool collects a screen-space region. Per SPEC §6.2, a region crossing a document window resolves to the board.
2. **(future)** The composition capture service renders content layers only. It reads the same `StrokeElement` data that `CommittedInkLayer` draws, and it skips `ImageWindow` chrome and `TeachingToolbar`.
3. **(future)** The capture becomes an image element kind on the resolved surface through a content operation, so it's undoable.
4. **(future)** Paste inside the app copies elements with fresh `ElementId` values and keeps display size, fitting uniformly when the target is smaller. Window moves during this flow change only `WindowLayoutState.Revision`.

### Minimize into desktop annotation with separate history

1. **(future)** Minimizing the main window, not a normal task switch, starts an `OverlaySession` in an App platform adapter.
2. The overlay reuses Core ink: `InputArbiter`, `EditingSession`, `StrokeElement`, and `EraseOperation`. It runs against its own surface and its own desktop-origin transform, so its points never mean application `ScreenPoint`.
3. **(future)** The overlay keeps its own history. Its strokes don't enter `Workspace` content, save, or global history, and exit discards them.
4. **(future)** A desktop screenshot excludes overlay tools, then returns to the main window's saved layout and inserts the image on the resolved surface as a normal operation.

### Constrained shapes and coordinate-system DAG

1. **(future)** A shape tool creates an `Element` subtype with semantic geometry (center and radius, or vertices plus constraint) and an `ElementTransform`. Today's shared store and `BeginElement` already accept the common boundary; no production shape kind or tool exists.
2. **(future)** A coordinate system is one composite element with its own local frame. Its children (points, lines, circles, conics) form a dependency DAG keyed by internal IDs.
3. **(future)** Resizing the frame relayouts children with equal units and perpendicular axes. Moving a point updates dependents, and deleting one lists cascade deletes before applying them. Each user action is one content operation.
4. The eraser keeps working on strokes only, so it can't damage shape or coordinate-system semantics.

### Independent whiteboard and recognition toggle

1. **(future)** Adding a whiteboard adds a `SurfaceKind` value and a `Surface` with its own camera, like today's `Workspace.BoardSurfaceId` with `BoardCamera`. It isn't a document and can't nest.
2. **(future)** Tools and `EditingSession` connect through the new surface kind's ownership and camera rules. Today `BeginElement` accepts the board or a registered, locked document page; it does not yet accept an independent whiteboard.
3. **(future)** With recognition turned on, an offline adapter selects strokes from a pending operation's `After` elements and, once the ink has committed, may propose a replacement operation. With it off, which is the default, nothing runs. Ink never waits on it, and an accepted result is a normal undoable operation.

## Phase-one verification

Pure Core tests cover these boundaries. Desktop behavior is not run in this environment and remains 发布后实际环境待测.

- `OwnershipCoordinateTests`: coordinate round trips, pending lock and cancel, normal and fill layouts sharing one content list, and camera moves leaving content and layout revisions alone. `Window_move_preserves_page_stroke_points_and_content_revision` snapshots committed page points, moves the owning window, and compares points and content revision directly.
- `StrokeEraseTests.Erase_is_limited_to_start_page_and_cancellation_restores_content`: erase stays on its start page and leaves the asset unchanged.
- `StrokeEraseTests.Erase_preserves_same_page_nonstroke_identity_and_data`: both batch and incremental erasers preserve a non-stroke fixture in the same page store while committing correct stroke fragments in one revision.
- `WindowLayoutTests`: window move, resize, fill, and close stay in layout or resource state.
