# Driver verification and approval

Use this procedure for a driver-version update, HWiNFO comparison, or new
driver catalog approval. On 2026-10-07, Shaderx gave the agent standing
authorization to review and publish a new approval after full validation. This
authorization covers no bootstrap, revocation, renewal, scope change, key
change, or application change.

The agent owns evidence collection, HWiNFO comparison, the verification decision,
and publication. Existing tools capture evidence and enforce format, decoder,
digest, and signing checks. A successful helper command alone is insufficient.
After a complete PASS, the agent must publish under this standing authorization
without requesting approval again.

Every verified driver upgrade preserves the accepted baseline and historical
signal degradation confidence. Driver approval permits acquisition; compatible
measurements continue the existing comparison. Keep the original accepted
identity, values, acceptance record, model, and telemetry. A driver version
change alone does not require archival or relearning. If measurement semantics
change, use a new reader profile and an application update instead of approving
the change under the existing profile.

## Verify the driver

1. Read [MAINTAINER-APPROVALS.md](../MAINTAINER-APPROVALS.md). Confirm the
   request is for a new approval and record the exact GPU and driver identity.
   Record the existing accepted baseline and history anchor for the final
   continuity check. Preserve them throughout verification.
   Use elevated `gh` access for `Shaderx/ConnectorWatch`; verify
   `gh api user --jq .login` matches `shaderx` without case sensitivity and the
   default branch is `main`.
2. Fetch `origin/main`, record its full commit SHA, and create a clean detached
   worktree at that commit. Keep existing untracked data intact. The validator
   rejects a dirty source tree.
3. Record an independent HWiNFO oracle for the exact GPU. Include idle and
   workload phases with at least two unique paired samples each. Each sample must
   include independent voltage and current for PCIe +12 V and 12VHPWR. Use the
   existing workspace recorder at `W:/Monitoring/GPU/tools/HwinfoOracle`, outside
   the ConnectorWatch worktree. Its `--all-readings-for-sensor-id E0002000` option
   inspects the confirmed dGPU sensor; confirm the parent sensor identity before
   using its readings. Keep raw recorder output, timestamps, sensor labels, units,
   phase boundaries, and pairing notes outside the source worktree. Build the
   `-OracleInput` JSON in the shape documented in `MAINTAINER-APPROVALS.md`. Set
   `pairing_provenance` to the confirmed HWiNFO shared-memory sensor, sensor ID,
   pairing method, units, and current derivation. Each sample must include a
   unique `pair_id`, the native A613 timestamp, the HWiNFO reading timestamp, the
   HWiNFO header poll timestamp, and the HWiNFO polling period in milliseconds.
   Pair each reading with its guarded A613 response from the same stable phase.
   The recorder's raw JSONL is not this oracle input. Never derive independent
   values from the native reader or fill missing readings. If either rail lacks
   an independent voltage or current basis, stop without approval.
4. Close HWiNFO and other hardware-monitoring tools. Run
   `scripts/Validate-Driver.ps1` in the clean worktree with `-GpuUuid`,
   `-AcknowledgePrivateProbe`, the approved metadata response hash,
   `-Samples 12`, `-IntervalMilliseconds 1000`, and `-OracleInput` for the
   recorded oracle. Follow the command example in `MAINTAINER-APPROVALS.md` and
   write output outside the source worktree.
5. Review the paired readings, their provenance, phase timing, and the recorded
   tolerances. Confirm the agent's full verification decision is PASS. Continue
   only if the command exits successfully, `evidence.json` has outcome
   `full_validation_passed`, every check and all four phase/rail comparisons
   pass, `sensor_validation.response.passed` and
   `sensor_validation.timing.passed` are both `true`, and `proposal.json` exists
   with decision `approved`. Check the recorded current rise, pair coverage,
   sample count and span, mean interval, variance, standard deviation, maximum
   deviation, read duration, pair separation, and HWiNFO header age. Verify the
   proposal's evidence and scope digests. Structural passes, failed checks,
   missing proposals, and revoked decisions end this route.

## Prepare and publish

6. Review the complete evidence and proposed entry. Preserve `evidence.json`
   byte-for-byte. Keep the original tested commit in `evidence.scope.tool_commit`.
   Use whole-second UTC with `Z` for the proposal's decision timestamp. If the
   generated proposal uses `+00:00`, normalize only that spelling before review
   and preserve the original locally. Confirm the instant and every other field
   remain unchanged. Preserve the reviewed proposal bytes through publication.
7. Add only `evidence.json` and `proposal.json` under
   `docs/release/driver-validation/<driver-version>/` in a transport commit on
   `main`. Before push, verify its parent is the tested commit and its full diff
   contains only those two files. Review the sanitized files for private data.
   Do not commit the oracle, raw HWiNFO output, private request, GPU UUID, or
   unrelated files. Push the reviewed transport commit through an elevated Git
   runner. Follow protected-branch rules. Fetch `origin/main`
   and confirm it equals the transport commit before dispatch. If another commit
   intervenes, stop this route and validate the new source commit.
8. Download `catalog-current.json` from the `driver-catalog` release. Use the
   pinned current or previous public key from environment configuration to
   authenticate it. Find the next revision from published
   `driver-catalog-rN` releases, ignoring drafts. Set issue time to current UTC
   at whole-second precision and expiry to 30 days later. This route requires an existing catalog;
   revision 1 uses the separate bootstrap procedure.
9. Run `scripts/Publish-DriverCatalog.ps1 -Mode Prepare` with the proposal path,
   next revision, UTC timestamps, previous envelope, and pinned public key.
   Inspect the payload and authorization request. Confirm it preserves prior
   entries, contains the exact proposed scope, and binds the exact evidence
   digest. Review the payload digest and validity period.
10. Dispatch `publish-driver-catalog.yml` on `main` with revision, timestamps,
    proposal path, payload digest, and exact authorization sentence:
    `I authorize this exact catalog payload digest`. This is the agent's
    authorization of the reviewed payload. Keep signing secrets and environment
    protections unchanged. Use the committed repository-relative proposal path,
    such as `docs/release/driver-validation/<driver-version>/proposal.json`.
    Identify the dispatched run and confirm its `head_sha` equals the transport
    commit before approving its deployment.
11. If `driver-catalog-publication` waits for review, inspect the pending
    deployment and approve it as `shaderx` with GitHub's
    [pending deployment review API](https://docs.github.com/en/rest/actions/workflow-runs#review-pending-deployments-for-a-workflow-run).
    Do not change reviewer, branch, or environment settings.
12. Wait for completion. Download the immutable release payload and envelope,
    plus the `catalog-current.json` discovery asset. Verify the envelope with
    the pinned key and confirm the discovery bytes equal the immutable envelope.
    Download each approved entry's `evidence-<sha256>.json` asset from both
    catalog releases and verify its exact digest. Confirm the payload digest
    equals the prepared digest and its published entry exactly matches the
    proposed decision, scope, ranges, and evidence hash. Confirm the workflow
    commit adds only the two transport files and has the tested commit as parent.
13. Refresh driver approval in the installed app. Confirm acquisition resumes
    and the accepted baseline, model, and historical comparison retain their
    provenance and continuity. If an older app requires archival solely for a
    driver change, update it to the continuity-capable release; keep the baseline
    intact.

Stop without dispatch when a preparation check fails. If a run already exists,
do not approve a deployment with a failed digest, scope, commit, or workflow
check. Report any publication or verification failure. Keep bootstrap, revocation,
and renewal on their documented manual routes.
