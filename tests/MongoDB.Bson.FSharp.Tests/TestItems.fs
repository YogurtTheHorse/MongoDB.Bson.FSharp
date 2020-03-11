module TestItems

type TestResult<'a> =
    | Failure
    | Result of cause: string * nr : 'a
    | NoResults of string
    
type TestRecord =
    {
      Foo: string
    }
type TestItem =
    {
      Id : int
      Name : string
      Salary : decimal
      Array : int array
      RecursiveOpt : TestItem option
      Union : TestResult<int>
      ComplexUnion : TestResult<TestRecord>
      MapStringInt : Map<string,int>
      MapIntString : Map<int,string>
      SetInt: int Set
      SetOptionInt: int option Set
    }
type TestItems =
    {
        testItems: TestItem list
    }

let testItem =
    {
        Id = 12345
        Name = "Adron"
        Salary = 13000m
        Array = [|10;20;30|]
        RecursiveOpt = None
        Union = Result ("just", 5)
        ComplexUnion = Result ("bar", {Foo = "foo"})
        MapStringInt = [("one", 1);("two", 2)] |> Map.ofList
        MapIntString = [(5, "five"); (1, "one")] |> Map.ofList
        SetInt = Set.empty
        SetOptionInt = Set.empty
    }

let testItems =
    let parent = testItem
    {
        testItems =
            [
                parent
                {
                    Id = 1234567890
                    Name = "Collider"
                    Salary = 26000.99m
                    Array = [||]
                    RecursiveOpt = Some parent
                    Union = NoResults "just because"
                    ComplexUnion = NoResults "tested already"
                    MapIntString = Map.empty
                    MapStringInt = Map.empty
                    SetInt = [9;8;7] |> Set.ofList
                    SetOptionInt = [Some 7; None; Some 8; None; Some 7] |> Set.ofList // deduplicates values
                }
            ]
    }

