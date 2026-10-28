/*
    A small, deterministic shop database for trying sql-mcp.

    sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "CREATE DATABASE SqlMcpDemo"
    sqlcmd -S "(localdb)\MSSQLLocalDB" -d SqlMcpDemo -i scripts/demo-database.sql
*/
SET NOCOUNT ON;

CREATE TABLE dbo.Customers (
    Id           int IDENTITY      CONSTRAINT PK_Customers PRIMARY KEY,
    Name         nvarchar(100)     NOT NULL,
    Email        nvarchar(200)     NOT NULL,
    City         nvarchar(60)      NOT NULL,
    PasswordHash varbinary(32)     NOT NULL,
    CreatedAt    datetime2(0)      NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT SYSUTCDATETIME());

CREATE TABLE dbo.Products (
    Id       int IDENTITY   CONSTRAINT PK_Products PRIMARY KEY,
    Name     nvarchar(100)  NOT NULL,
    Category nvarchar(40)   NOT NULL,
    Price    decimal(10, 2) NOT NULL CONSTRAINT CK_Products_Price CHECK ([Price] >= (0)));

CREATE TABLE dbo.Orders (
    Id         int IDENTITY CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId int          NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (Id),
    PlacedAt   datetime2(0) NOT NULL,
    Status     varchar(12)  NOT NULL);

CREATE TABLE dbo.OrderLines (
    OrderId    int            NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES dbo.Orders (Id),
    LineNumber tinyint        NOT NULL,
    ProductId  int            NOT NULL CONSTRAINT FK_OrderLines_Products REFERENCES dbo.Products (Id),
    Quantity   smallint       NOT NULL,
    UnitPrice  decimal(10, 2) NOT NULL,
    CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber));

EXEC sys.sp_addextendedproperty N'MS_Description', N'Registered shoppers.', N'SCHEMA', N'dbo', N'TABLE', N'Customers';
EXEC sys.sp_addextendedproperty N'MS_Description', N'One row per checkout.', N'SCHEMA', N'dbo', N'TABLE', N'Orders';
EXEC sys.sp_addextendedproperty N'MS_Description', N'Pending, Paid, Shipped or Cancelled.', N'SCHEMA', N'dbo', N'TABLE', N'Orders', N'COLUMN', N'Status';

WITH n AS (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.Customers (Name, Email, City, PasswordHash)
SELECT CONCAT(N'Customer ', i), CONCAT(N'customer', i, N'@example.com'),
       CHOOSE(1 + i % 6, N'Indore', N'Pune', N'Bengaluru', N'Delhi', N'Hyderabad', N'Mumbai'),
       HASHBYTES('SHA2_256', CONCAT(N'demo-', i))
FROM n WHERE i <= 200;

WITH n AS (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
INSERT dbo.Products (Name, Category, Price)
SELECT CONCAT(N'Product ', i), CHOOSE(1 + i % 4, N'Books', N'Electronics', N'Home', N'Toys'), 99 + (i * 37) % 900
FROM n;

WITH n AS (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.Orders (CustomerId, PlacedAt, Status)
SELECT 1 + (i * 7) % 200, DATEADD(hour, -i * 3, '2026-10-01'),
       CHOOSE(1 + i % 10, 'Paid', 'Shipped', 'Shipped', 'Shipped', 'Paid', 'Pending', 'Shipped', 'Cancelled', 'Shipped', 'Paid')
FROM n;

WITH l AS (SELECT v AS LineNumber FROM (VALUES (1), (2), (3)) AS t (v))
INSERT dbo.OrderLines (OrderId, LineNumber, ProductId, Quantity, UnitPrice)
SELECT o.Id, l.LineNumber, p.Id, 1 + (o.Id + l.LineNumber) % 3, p.Price
FROM dbo.Orders o
JOIN l ON l.LineNumber <= 1 + o.Id % 3
JOIN dbo.Products p ON p.Id = 1 + (o.Id * 13 + l.LineNumber * 5) % 40;
