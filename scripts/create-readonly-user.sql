/*
    Least-privilege login for sql-mcp.

    sql-mcp refuses writes on its own, but the database's permissions are the boundary that holds even if
    every check in sql-mcp were bypassed. Run this as an administrator after replacing the placeholders.
*/

-- SQL Server: a server login mapped to a database user.
USE [master];
CREATE LOGIN [sql_mcp_reader] WITH PASSWORD = N'<a strong, unique password>', CHECK_POLICY = ON;
GO

USE [<YourDatabase>];
CREATE USER [sql_mcp_reader] FOR LOGIN [sql_mcp_reader];

-- Azure SQL Database: use a contained user instead of the two statements above.
-- CREATE USER [sql_mcp_reader] WITH PASSWORD = N'<a strong, unique password>';

ALTER ROLE [db_datareader] ADD MEMBER [sql_mcp_reader];

-- explain_query asks SQL Server for an estimated plan, which needs SHOWPLAN.
GRANT SHOWPLAN TO [sql_mcp_reader];

-- Optional: enforce in the database what sql-mcp's DeniedTables and SensitiveColumns enforce in the server.
-- DENY SELECT ON SCHEMA::[audit] TO [sql_mcp_reader];
-- DENY SELECT ON [dbo].[Users] ([PasswordHash], [MfaSecret]) TO [sql_mcp_reader];
GO
