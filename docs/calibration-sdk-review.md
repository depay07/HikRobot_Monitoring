# Global-variable calibration review — 2026-11-11

## Supported SDK route

Installed embedded VM Windows SDK 4.4.50 .NET documentation explicitly includes SC6000 and documents GlobalVariableModuleTool.SetGlobalVar, GetVarFloat and GetAllGlobalVar for a connected solution. Source: the installed embedded SDK development guide CHM, global-module operations and GlobalVariableModuleTool API pages.

The monitor now writes four selected positive scalar float globals using SetGlobalVar, then reads GetVarFloat to verify. GetAllGlobalVar reads the serialization-oriented ValueList; Save checks its selected values against runtime values before and after saving. The existing CurrentRecipeSave workflow resolves the device's current full solution path and uses SaveAs at exactly that path. No local Save As dialog or configured startup recipe is used.

The old code that queried every Calculator expression and rewrote command#start/data/io has been removed. Previous offline reproduction of the SDK's additive output cache did not establish the cause or resolution of the field camera's expression-read failure.

## Target selection and mathematics

The mapping is explicit and keyed by camera IP, procedure and current remote recipe path. It contains only names/IDs, not coefficients. Users choose four variables and raw L2LAbsDist/CircleRadius sources. Selected raw source and global must have scalar subscriptions into the same Calculator. Native input subscriptions are compared again before reads/writes/save. This establishes wiring, not the multiplication expression's semantics; one-time VisionMaster recipe preparation is required (CALIBRATION.md).

Raw callbacks with valid status and exactly one float are joined by execution count. CircleRadius is doubled into pixel diameter. All four stored factors are actual millimetres / pixel length, canonicalized to SDK float precision. Later triggers cannot replace a captured reference. Invalid, self-executed or error measurements are excluded.

Readback mismatch/partial writes restore only the selected globals. A failed save triggers restoration and another save of the originals; failed recovery stays paused. Recipe/context changes block further writes. Other global values, Calculator expressions, condition thresholds, image acquisition and NG capture logic are not edited.

## Validation and limits

Run tests/RunCalibrationTests.ps1. It builds Release and checks arithmetic (14/500), renamed labels, float precision, bad measurements, partial writes, readback/save failures, rollback, changed recipe/coefficient rejection, camera/recipe/flow key separation, UI behavior, real SDK callback shapes, native inventory decoding and public global API signatures. CurrentRecipeSave tests check overwriting the current remote path and rejecting a changed path. The UI test uses a simulated session, not a connected device.

No production camera was connected. No supplied solx was edited or run locally. These tests cannot prove native device transport, the exact prepared recipe multiplication, persistence after camera restart, or production trigger timing. Those remain explicit field acceptance checks; no claim of successful hardware calibration is made.