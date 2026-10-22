namespace Shiny.EntityFrameworkCore.AI;

/// <summary>
/// The operations an LLM may perform on one registered entity. Each flag produces exactly one tool; a flag that is
/// not set produces nothing, so the model never even learns the operation exists.
/// </summary>
[Flags]
public enum EntityAICapabilities
{
    None      = 0,

    /// <summary><c>{entity}_get_by_id</c> — fetch one row by primary key, through the scope.</summary>
    Get       = 1 << 0,

    /// <summary><c>{entity}_query</c> — structured filter, sort and capped paging, executed as SQL.</summary>
    Query     = 1 << 1,

    /// <summary><c>{entity}_count</c> — count rows matching an optional filter, executed as SQL.</summary>
    Count     = 1 << 2,

    /// <summary><c>{entity}_aggregate</c> — count/sum/min/max/avg over a field, executed as SQL.</summary>
    Aggregate = 1 << 3,

    /// <summary><c>{entity}_insert</c> — add one row; it must satisfy the scope.</summary>
    Insert    = 1 << 4,

    /// <summary><c>{entity}_update</c> — patch one row; it must be in scope before and after.</summary>
    Update    = 1 << 5,

    /// <summary><c>{entity}_delete</c> — remove one row, found through the scope.</summary>
    Delete    = 1 << 6,

    /// <summary>Everything that only reads. The default when no capability is chosen.</summary>
    ReadOnly  = Get | Query | Count | Aggregate,

    /// <summary>Everything that mutates. Hosts gate on this rather than testing three flags.</summary>
    Write     = Insert | Update | Delete,

    /// <summary>All seven operations.</summary>
    All       = ReadOnly | Write
}
