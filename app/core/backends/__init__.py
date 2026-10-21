from ..models import SiteConfig
from .base import Backend


def make_backend(site: SiteConfig) -> Backend:
    if site.db_type == "ORACLE":
        from .oracle_backend import OracleBackend
        return OracleBackend(site)
    from .mssql_backend import MssqlBackend
    return MssqlBackend(site)
