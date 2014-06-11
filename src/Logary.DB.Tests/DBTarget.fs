﻿module Logary.DB.Tests

open Fuchu

open Logary
open Logary.Configuration
open Logary.Target
open Logary.Targets
open Logary.Metric

open System
open System.Data
open System.Data.SQLite
open System.Net

/// this one is a per-process db
[<Literal>]
let private inMemConnStrShared = "FullUri=file::memory:?cache=shared"

/// this one cleans out the database when you close the connection
[<Literal>]
let private inMemConnStrEmpheral = "FullUri=file::memory:"

/// some methods for working with SQLite databases in memory with connection
/// management nicely handled
module SQLiteDB =

  let private consoleAndDebugger =
    { new Logger with
        member x.Log line =
          let fm = Formatting.StringFormatter.VerbatimNewline
          let str = fm.format line
          if not <| String.IsNullOrWhiteSpace str then
            System.Console.Write(str)
            System.Diagnostics.Debugger.Log(6, "tests", str)
        member x.Metric _ = ()
        member x.Name = "DB test logger"
        member x.Level = LogLevel.Verbose }

  open System
  open Logary.DB.Migrations

  open FluentMigrator
  open FluentMigrator.Runner
  open FluentMigrator.Runner.Processors
  open FluentMigrator.Runner.Processors.Sqlite
  open FluentMigrator.Runner.Generators.SQLite

  /// wrap the connection in a non-closing delegator for connections that are
  /// managed externally
  let wrapConnNoClose (inner : IDbConnection) =
    { new IDbConnection with
        member x.BeginTransaction() =
          inner.BeginTransaction()
        member x.BeginTransaction isolationLevel =
          inner.BeginTransaction isolationLevel
        member x.ChangeDatabase newDb =
          inner.ChangeDatabase newDb
        member x.ConnectionString
          with get() = inner.ConnectionString
          and set v = inner.ConnectionString <- v
        member x.CreateCommand () =
          inner.CreateCommand ()
        member x.ConnectionTimeout = inner.ConnectionTimeout
        member x.Close() = ()
        member x.Dispose() = ()
        member x.Database
          with get() = inner.Database
        member x.Open () =
          inner.Open()
        member x.State
          with get() = inner.State }

  /// used to avoid closing the SQLite connection in between migrations
  type NonClosingSqliteProcessorFactory(conn : IDbConnection) =
    inherit MigrationProcessorFactory()
    override x.Create (connStr : string, accouncer : IAnnouncer, opts : IMigrationProcessorOptions) =
      new SqliteProcessor(wrapConnNoClose conn,
                          new SqliteGenerator(), accouncer, opts,
                          new SqliteDbFactory())
      :> IMigrationProcessor

  let private openConnInner connStr () =
    let conn = new SQLiteConnection(connStr : string) :> IDbConnection
    conn.Open()
    conn

  /// open and migrate with the given connection string
  let openConn connStr =
    Log.info consoleAndDebugger "openConn"
    let conn = openConnInner connStr ()
    Runner(NonClosingSqliteProcessorFactory(conn), connStr, logger = consoleAndDebugger).MigrateUp()
    conn

  let connMgrShared = Sql.withNewConnection (fun () -> openConn inMemConnStrShared)

[<Tests>]
let targetTests =
  let flush = flushTarget >> Async.Ignore >> Async.RunSynchronously

  let stop = shutdownTarget >> Async.Ignore >> Async.RunSynchronously

  let start f_conn =
    let conf = DB.DBConf.Create f_conn
    initTarget { serviceName = "tests" } (DB.create conf "db-target")

  testList "db target" [
    testCase "smoke" <| fun _ ->
      use c = new SQLiteConnection(inMemConnStrEmpheral) :> IDbConnection
      c.Open()
      c.Close()

    testCase "initialise" <| fun _ ->
      stop (start (fun () -> SQLiteDB.openConn inMemConnStrEmpheral))

    testCase "initialise and log" <| fun _ ->
      let target = start (fun () -> SQLiteDB.openConn inMemConnStrEmpheral)
      try (LogLine.Create "hello world") |> logTarget target
      finally stop target

    testCase "log and read back returns result" <| fun _ ->
      Tests.skiptest "log and read back not impl"
      
    testCase "log and read back: good field contents" <| fun _ ->
      Tests.skiptest "log and read back not impl"

    testCase "initialise and metric" <| fun _ ->
      let target = start (fun () -> SQLiteDB.openConn inMemConnStrEmpheral)
      try (Metric.Counter.counterValue "app.signin" 3.0) |> metricTarget target
      finally stop target

    testList "metric and read back returns result" [
      use db = SQLiteDB.openConn inMemConnStrShared
      let mgr = Sql.withConnection db

      let countSql = "SELECT COUNT(*) FROM Metrics m"

      // pre-conditions
      let count = Sql.execScalar mgr countSql [] |> Option.get
      Assert.Equal("count should be zero", 0L, count)

      // given
      let target = start (fun () -> db)
      (Metric.Counter.counterValue "web01.app.signin" 3.0) |> metricTarget target
      (Metric.Counter.counterValue "web02.app.signin" 6.0) |> metricTarget target
      flush target

      // then
      try
        let count = Sql.execScalar mgr countSql [] |> Option.get
        Assert.Equal("count should be two metrics", 2L, count)

        let records = Sql.execReader mgr "SELECT * FROM Metrics" [] |> Sql.map Sql.asMap
        let read (m : Map<string, obj>) k =
          try m.[k] :?> 'a
          with :? InvalidCastException as e ->
            Tests.failtestf "converting key %s to %s failed, was: %s."
              k typeof<'a>.Name (m.[k].GetType().Name)

        for r in records do
          let read k : 'a = read r k
          Assert.Equal("should have host name from computer DNS", Dns.GetHostName(), read "Host")
          Assert.StringContains("should have path from from metric", ".app.signin", read "Path")
          Assert.Equal("should have info level", int64 (Info.ToInt()), read "Level")
          Assert.Equal("should have counter type", DB.typeAsInt16 MetricType.Counter |> int64, read "Type")
          Assert.Equal("value is 3 or 6", true, read "Value" = 3.M || read "Value" = 6.M)
      finally
        stop target
      ]

    testCase "metric and read back: good field contents" <| fun _ ->
      Tests.skiptest "metric and read back not impl"
    ]

open Logary.DB.Migrations
open FluentMigrator.Runner.Processors

[<Tests>]
let migrationTests =
  let fac = Sqlite.SqliteProcessorFactory()
  let forgetful = "FullUri=file::memory:"

  testList "migrating sqlite db up and down" [
    testCase "migrating up" <| fun _ ->
      Runner(fac, forgetful).MigrateUp()

    testCase "migrating up with reading index" <| fun _ ->
      Runner(fac, forgetful).MigrateUp(Runner.IndexForReading)

    testCase "migating down" <| fun _ ->
      Runner(fac, forgetful).MigrateDown()

    testCase "migating down with reading index" <| fun _ ->
      Runner(fac, forgetful).MigrateDown(Runner.IndexForReading)
    ]

[<EntryPoint>]
let main args = defaultMainThisAssembly args