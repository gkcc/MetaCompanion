class SolverError(Exception):
    """Base class for errors safe to expose through the local API."""


class SchemaError(SolverError):
    def __init__(self, path: str, message: str):
        super().__init__(f"{path}: {message}")
        self.path = path
        self.message = message


class IllegalActionError(SolverError):
    pass


class DuplicateRequestError(SolverError):
    pass


class ResultObservationConflictError(SolverError):
    """A game already has different durable terminal-result content."""
