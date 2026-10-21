class BackendError(Exception):
    """A failure that should be shown to the user as-is."""


class ManualConnectionRequired(BackendError):
    """No Oracle tnsnames.ora could be located; ask the user for details."""


class RunCancelled(Exception):
    """Raised inside a run when the user pressed Cancel."""
