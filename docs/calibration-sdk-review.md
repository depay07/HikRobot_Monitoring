# Calibration device inventory review — 2026-11-10

## Confirmed SDK behavior

Installed SDK: VisionMaster 4.4.50. VM.Core.dll SHA-256:
`6698828cec20db765d814a1561d1c6d709985b4fc38017844043346aa0a07323`.
REA identity evidence: `ev_75a797de4e825a1a9b44541dd4793ce343ebb5d40c8ac01c41791822537dbe41` (static metadata only).

- CalculatorModule.xml defines `%var0%` as a non-result-show dynamic output.
- VM.Core.VmModule.Outputs loads outputs lazily through InitOutputDynamicIO.
- InitDynamicIO calls AnalysisIOInfo for returned entries. AnalysisIOInfo adds an entry when its name is absent; it never removes old entries. The loaded flag prevents subsequent reads from refreshing this cache.
- tests/CalibrationInventoryTests.cs executes this actual SDK parser against an isolated managed collection, first with `%var0%`, then a renamed item. Both remain. It starts no SDK engine and connects to no camera.
- ModuleParam's indexer and IUserStringData use the same native parameter read. Changing the cast is not a remedy.
- CalculatorControl.Update writes all items via byte-data command#start/command#data, then string command#io. The monitor retains this protocol. Skipping an unreadable item could delete it, so failure must stop the update.

The screenshot alone does not prove that the installed camera's var0 failure was caused by this cache. It does demonstrate a failure at that name; the stale-cache bug is independently reproduced and corrected here.

## Change

Query native output metadata for each read, consume only top-level dynamic non-display entries, and read every item. Check each payload name/type against the native inventory; compare inventories again after reading. Additional calculations and renamed items are retained. A real device item named var0 is handled normally, and a failure reading it still blocks saving.

Use native input metadata/subscriptions for measurement source selection and for before/after binding checks. Scalar line distance and circle radius are supported; constant inputs, ambiguous sources and variable/nonzero array indices are not treated as scalar pixel measurements. Raw source callback values are joined by execution number as before.

GetModuOutputIOInfo/GetModuInputIOInfo return an SDK-owned list whose finalizer frees nested buffers. Decode keeps the list alive throughout copying and does not manually free those buffers. GetMultiModuleSubscribe returns the SDK's managed clone.

## Verification and limits

- 23 inventory/source tests, including actual SDK additive-cache reproduction.
- 48 calibration arithmetic, preservation, rollback and UI tests.
- 8 installed-SDK result callback tests.
- Release and Debug builds.

Native camera transport, real parameter writes, camera recipe persistence and post-restart measurement are not established by these tests. No production camera was connected and no supplied solx was modified. The original 01.solx contains camera, trigger and communication modules; loading/running it locally was intentionally avoided because that would initialize external I/O.
