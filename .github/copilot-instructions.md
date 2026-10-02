# Repository instructions

- Preserve the WPF MVVM boundary; native view initialization and pointer interaction may remain in view code-behind.
- Keep capture, audio, encoding, and portable infrastructure in their existing projects.
- Do not replace DXGI/WASAPI operations with simulated data.
- Maintain safe partial-file finalization and explicit recording state transitions.
- Run `scripts\build.ps1` after changes that affect production code.
