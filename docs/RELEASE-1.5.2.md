# ConnectorWatch 1.5.2

Automatic reference acceptance is now enabled by default. New configurations
and existing configurations without `AutoAcceptReference` automatically accept
a qualified learned reference once the voltage model has usable evidence.
An explicitly saved off preference remains off. The checkbox and manual
**Accept reference** button remain available in **Reference & analysis**.

Accepted baselines remain frozen, and the existing identity, source health,
qualification, and legacy migration checks still apply.

The installer is now named **ConnectorWatch-Setup-1.5.2.exe**. Versioned releases,
the stable download, and signed update metadata use the versioned filename.
The unversioned download alias remains available for existing links.

Configuration, references, and recorded history are preserved on upgrade. This
patch does not change native hardware support or the signing identity.
