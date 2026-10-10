# StressDeez

**Created by AI:** StressDeez was created by ChatGPT from user-provided requirements. Its code, interface, and documentation were generated and iterated with AI. This repository publishes the V10 “Steady Power / Static UI” source.

AI authorship does not imply that the application has been independently audited or validated on every supported hardware configuration.

## V10 — steady-power controller

A portable, single-file Windows 11 x64 application using NVIDIA OpenCL compute,
multithreaded CPU AVX/FMA FFT workloads, and live NVIDIA NVML / optional CPU sensor telemetry.

## Build

Double-click **BUILD.bat**. It reuses an existing .NET 8+ SDK or downloads .NET 8 to a shared user folder. Once built, copy **dist/StressDeez.exe** anywhere on the same PC; it needs no .NET runtime installation. Your NVIDIA graphics driver must provide OpenCL and NVML. The build uses NuGet for `LibreHardwareMonitorLib` on first restore. The compiler output is saved to `build.log`.

## Controls

- **0–420 W slider:** 0 stops the compute workload. Above 0 the GPU initializes and takes priority, followed by CPU FFT work when measured GPU capacity is insufficient and combined CPU/GPU telemetry is available.
- **Blue CPU line:** Visible immediately. Drag onto it to snap to and pin **full GPU / zero CPU FFT**. The pin remains as the line moves; drag away to unpin.
- **67 W / 69 W:** Visible marked ticks on slider.
- **Click rounded top X:** Close immediately (no exit animation).
- **Arrow keys:** Adjust the target ±5 W; **Esc** sets 0 W; **F1** shows CPU sensor diagnostics.

## What's new in V10

- Constant OpenCL iteration count and number of enqueued kernels at *every* GPU effort level; GPU occupancy varies progressively, without the old sudden 30%/75% batch changes.
- Feedback controller samples approximately once per second, filters watt readings, waits ~1.9 seconds between adjustments, applies small bounded corrections and a ±4.5 W GPU / ±6 W CPU-phase deadband.
- Reversal damping reduces overshoot when measured power crosses the setpoint; reductions in CPU effort occur before GPU effort changes.
- Maintains the minimum continuously active GPU power-floor behavior introduced in V9. Below this floor, the GPU remains idle, not rapidly switched on/off.
- Removed all UI wobble, bounce, white explosion, pulsing, particles and animated wattage tweening. The display simply redraws measurements every 350 ms, in a tidier flat illustrated design.
- Preserves GPU-only pin mode, CPU/GPU temperature labels, truthful N/A sensor readings, custom icon, quote, and custom-shaped borderless window.

## Measurement limitations

This is **power-target feedback**, not a hardware power limiter. GPU boost states, fan/temperature effects, driver scheduling, transient loads, and the update cadence of sensor readings mean the app cannot guarantee an exact watt reading at every instant. Targets below the hardware's active-compute power floor or above its available power draw cannot be held continuously. There is no application-defined thermal cutoff; device BIOS, firmware and driver protections are unchanged. If CPU package-power data are unavailable, the app explicitly shows N/A, uses GPU-only wattage feedback, and cannot accurately regulate combined GPU + CPU power.

## Verification

Source checks can be performed on any OS. Windows compilation and hardware power-control accuracy must be verified on the target Windows 11 PC. If compilation fails, `build.log` has the compiler error. Use F1 diagnostics if CPU power/temperature read N/A.
