---
status: accepted
---

# Share companion-process invocation and bounded cleanup

This decision supersedes the language-server-local lifecycle ownership in ADR
0014 and refines the CLI subprocess lifetime in ADR 0027. It designates
`VbaTools.ProcessInvocation` as a neutral foundation under ADR 0039.

Language-server capability/reference discovery and debug-adapter capability
probes/snapshot builds use one `ProcessInvocation` Module outside both product
trees. Consumers pin an absolute executable and supply ordered arguments and a
cancellation token. The result contains exit code, stdout, and stderr; a nonzero
exit remains data. Consumers retain command arguments, JSON interpretation,
capability policy, diagnostics, and snapshot/generation ownership.

The ordinary System Process Adapter uses no shell, inherits its working directory,
and retains the platform's default output encoding. The DAP Adapter uses existing
atomic Windows Job/suspended-launch primitives, the executable's directory, and
UTF-8 readers with BOM detection. Job ownership exists before process execution.
The common lifecycle starts both drains before the DAP primary thread is resumed
exactly once, then supervises exit and each reader concurrently. Cancellation
before start launches nothing; cancellation remains effective during post-exit
draining and immediately before a normal result is published.

Cancellation, a synchronous startup/read/resume failure after acquiring a handle,
or an asynchronous execution/read failure requests owned-process termination
once. Cleanup waits without the caller's cancelled token for terminal exit and
both readers to settle. A reader that already failed remains the primary failure;
it is not reclassified as a successful output capture. Proven cleanup preserves
ordinary cancellation or the original failure. An already-exited termination
race is benign only after terminal exit and reader completion are proved.

The existing five-second LSP cleanup budget becomes the shared asynchronous
cleanup budget. A missed deadline, failed terminal proof, or additional cleanup
failure produces `ProcessLifecycleException`, retaining original cancellation or
execution failure, termination failure, and cleanup evidence. Multiple cleanup
failures are retained together. Adapters release handles without another exit
wait; late Tasks remain observed. Release itself cannot convert unproven cleanup
to success or overwrite the original failure with a secondary exception.

The five seconds do not limit normal build duration or user interaction, are not
a new user setting, and are not a strict wall-clock bound for synchronous OS
calls. This decision adds no Job active-process accounting. Catalog shutdown
includes the shared budget before abandoning non-cooperative registry work.

The DAP CLI Adapter does not reuse `DebugExcelProcessOwner.DisposeAsync`, whose
unbounded ownership completion is appropriate to a different session contract.
The visible Excel session lifetime, snapshot release policy, and one-shot debug
generation admission remain separate and unchanged. Partial startup transfers
process, thread, and pipe ownership only after the launch is complete.

Both products reference and publish the neutral assembly. Architecture guards
reject reverse product dependencies from its production and test projects.
Lifecycle tests belong to the foundation; concrete ownership tests belong to
each Adapter. No product links another product's test source. Normal test and
release scripts run the foundation suite, and publish/package verification
checks both consuming executables.
