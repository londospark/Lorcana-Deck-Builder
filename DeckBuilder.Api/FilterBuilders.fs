namespace DeckBuilder.Api

open Qdrant.Client.Grpc

/// Composable filter builders for Qdrant queries
module FilterBuilders =
    
    /// Create a color filter for one or more colors (OR logic)
    let colorFilter (colors: string list) : Filter =
        if colors.IsEmpty then
            null
        else
            let filter = Filter()
            for color in colors do
                let cond = Condition()
                let fc = FieldCondition()
                fc.Key <- "inkColor"
                let m = Match()
                m.Keyword <- color
                fc.Match <- m
                cond.Field <- fc
                filter.Should.Add(cond)
            filter
    
    /// Create a format filter (Core vs Infinity)
    let formatFilter (format: DeckBuilder.Shared.DeckFormat) : Filter =
        match format with
        | DeckBuilder.Shared.DeckFormat.Core ->
            let filter = Filter()
            for setNum in [1..6] do
                let cond = Condition()
                let fc = FieldCondition()
                fc.Key <- "setNum"
                let m = Match()
                m.Integer <- int64 setNum
                fc.Match <- m
                cond.Field <- fc
                filter.Should.Add(cond)
            filter
        | DeckBuilder.Shared.DeckFormat.Infinity ->
            null
    
    /// Create a cost range filter
    let costFilter (minCost: int option) (maxCost: int option) : Condition =
        match minCost, maxCost with
        | None, None -> null
        | Some min, None ->
            let cond = Condition()
            let fc = FieldCondition()
            fc.Key <- "inkCost"
            let range = Range()
            range.Gte <- float min
            fc.Range <- range
            cond.Field <- fc
            cond
        | None, Some max ->
            let cond = Condition()
            let fc = FieldCondition()
            fc.Key <- "inkCost"
            let range = Range()
            range.Lte <- float max
            fc.Range <- range
            cond.Field <- fc
            cond
        | Some min, Some max ->
            let cond = Condition()
            let fc = FieldCondition()
            fc.Key <- "inkCost"
            let range = Range()
            range.Gte <- float min
            range.Lte <- float max
            fc.Range <- range
            cond.Field <- fc
            cond
    
    /// Create an inkable filter
    let inkableFilter (inkable: bool option) : Condition =
        match inkable with
        | None -> null
        | Some value ->
            let cond = Condition()
            let fc = FieldCondition()
            fc.Key <- "inkwell"
            let m = Match()
            m.Boolean <- value
            fc.Match <- m
            cond.Field <- fc
            cond
    
    /// Build a complete filter from individual components
    let buildFilter 
        (colors: string list) 
        (format: DeckBuilder.Shared.DeckFormat)
        (minCost: int option)
        (maxCost: int option)
        (inkable: bool option) : Filter =
        
        let mainFilter = Filter()
        
        // Add color filter (OR logic, goes into Must as a nested filter)
        let colFilter = colorFilter colors
        if not (isNull colFilter) then
            let cond = Condition()
            cond.Filter <- colFilter
            mainFilter.Must.Add(cond)
        
        // Add format filter (OR logic, goes into Must as a nested filter)
        let fmtFilter = formatFilter format
        if not (isNull fmtFilter) then
            let cond = Condition()
            cond.Filter <- fmtFilter
            mainFilter.Must.Add(cond)
        
        // Add cost filter (direct condition)
        let costCond = costFilter minCost maxCost
        if not (isNull costCond) then
            mainFilter.Must.Add(costCond)
        
        // Add inkable filter (direct condition)
        let inkCond = inkableFilter inkable
        if not (isNull inkCond) then
            mainFilter.Must.Add(inkCond)
        
        if mainFilter.Must.Count = 0 then null else mainFilter
