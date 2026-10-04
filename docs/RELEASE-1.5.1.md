# ConnectorWatch 1.5.1

This patch release makes reference acceptance visible and deliberate in the
dashboard while keeping the existing monitoring and native-reader scope.

- **Accept reference** is available when a learned candidate has enough
  qualified observations and a compatible identity. Acceptance freezes the
  reference used for later comparisons.
- **Automatically accept qualified reference** is an opt-in setting. It is off
  by default, is saved as `AutoAcceptReference`, applies without restarting,
  and continues while the dashboard is closed. It never replaces an accepted
  reference or migrates a legacy baseline.
- Reference learning now explains whether it is waiting for steady observations,
  waiting for a fitted voltage model, ready for acceptance, or already active.
  “Reference pending” means that acceptance is still required; additional days
  alone do not complete that step.
- Existing configuration, accepted references, recordings, and the supported
  native-reader boundary remain unchanged. This release does not change the
  private native ABI or expand hardware support.

## Validation

The local daemon self-test passed, including 26 authenticated application-update
checks, 40 behavioral checks, and reference-acceptance behavior. The GUI data
suite passed 129 checks, and the synthetic UI suite passed 19 checks. The
protected release workflow verifies the signed installer before publication.

The Windows installer uses the same project self-signed publisher model as 1.5.0;
Windows may show an unknown-publisher prompt. Follow the [installation and
signature verification instructions](https://github.com/Shaderx/ConnectorWatch/blob/main/docs/INSTALLATION.md)
before running it.
