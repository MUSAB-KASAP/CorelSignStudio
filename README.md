# Corel Sign Studio

Corel Sign Studio is a .NET 8 WPF desktop application for producing sign designs in CorelDRAW 2026 through late-bound COM automation.

## Solution boundaries

- `CorelSignStudio.App`: WPF composition root and user interface.
- `CorelSignStudio.Domain`: editor-independent design model; every coordinate and physical size is expressed in millimetres.
- `CorelSignStudio.Corel`: the only project allowed to access CorelDRAW COM. All COM calls are serialized on a dedicated STA thread.
- `CorelSignStudio.Templates`: reusable sign specifications and asset references.
- `CorelSignStudio.Storage`: local persistence and output-path services.
- `CorelSignStudio.Tests`: unit tests and the opt-in, real-Corel integration smoke test.

The first milestone intentionally leaves the generated WPF shell unchanged. UI implementation starts only after the solution build and real CorelDRAW connection/save/export test have been reported.

