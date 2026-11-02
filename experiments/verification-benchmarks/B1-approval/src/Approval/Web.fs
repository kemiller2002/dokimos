namespace Approval

open System
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting

/// Server-rendered HTML views. Every piece of user text is HTML-encoded.
module internal Views =

    let enc (s: string) = WebUtility.HtmlEncode(if isNull s then "" else s)

    let stateName =
        function
        | Draft -> "Draft"
        | Submitted -> "Submitted"
        | Approved -> "Approved"
        | Rejected -> "Rejected"

    /// The commands that are legal in a state, by form value, derived from Domain.decide.
    let legalCommands (request: Request) =
        [ "Submit", Submit; "Approve", Approve ""; "Reject", Reject ""; "Revise", Revise ]
        |> List.filter (fun (_, cmd) -> Domain.decide cmd request |> Result.isOk)
        |> List.map fst

    let page (title: string) (body: string) =
        $"""<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{enc title}</title></head>
<body>
{body}
</body>
</html>"""

    let home () =
        page
            "Approvals"
            """<h1>Approvals</h1>
<form data-testid="create" method="post" action="/requests">
  <label>Title <input type="text" name="title" required></label>
  <button type="submit" data-testid="create-submit">Create</button>
</form>"""

    type Banner =
        | NoBanner
        | ConflictBanner
        | ErrorBanner of string

    let requestPage (request: Request) (banner: Banner) =
        let legal = legalCommands request

        let bannerHtml =
            match banner with
            | NoBanner -> ""
            | ConflictBanner ->
                """<p role="alert" data-testid="conflict">Conflict: this request was changed by someone else. The current version is shown below.</p>"""
            | ErrorBanner msg -> $"""<p role="alert" data-testid="error">{enc msg}</p>"""

        let noteInput =
            if legal |> List.exists (fun c -> c = "Approve" || c = "Reject") then
                "  <label>Note <input type=\"text\" name=\"note\"></label>\n"
            else
                ""

        let buttons =
            legal
            |> List.map (fun c ->
                $"""  <button type="submit" name="command" value="{c}" data-testid="cmd-{c}">{c}</button>{'\n'}""")
            |> String.concat ""

        let id = enc request.Id

        page
            request.Title
            $"""<p><a href="/">New request</a></p>
{bannerHtml}
<h1 data-testid="title">{enc request.Title}</h1>
<dl>
  <dt>State</dt><dd data-testid="state">{stateName request.State}</dd>
  <dt>Version</dt><dd data-testid="version">{request.Version}</dd>
  <dt>Note</dt><dd data-testid="note">{enc request.Note}</dd>
</dl>
<form data-testid="command-form" method="post" action="/requests/{id}/command">
  <input type="hidden" name="version" value="{request.Version}">
{noteInput}{buttons}</form>"""

    let notFound () =
        page "Not found" """<h1>Not found</h1><p data-testid="error">No such request.</p><p><a href="/">Home</a></p>"""

module Web =

    let private validId = Regex(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled)

    let private html (status: int) (body: string) : IResult =
        Results.Content(body, "text/html; charset=utf-8", Text.Encoding.UTF8, Nullable status)

    let private seeOther (location: string) : IResult =
        { new IResult with
            member _.ExecuteAsync(ctx: HttpContext) =
                ctx.Response.StatusCode <- StatusCodes.Status303SeeOther
                ctx.Response.Headers.Location <- Microsoft.Extensions.Primitives.StringValues location
                Task.CompletedTask }

    let private field (form: IFormCollection) (name: string) =
        match form.TryGetValue name with
        | true, v when v.Count > 0 && not (isNull v[0]) -> Some v[0]
        | _ -> None

    let private parseCommand (name: string option) (note: string) =
        match name with
        | Some "Submit" -> Some Submit
        | Some "Approve" -> Some(Approve note)
        | Some "Reject" -> Some(Reject note)
        | Some "Revise" -> Some Revise
        | _ -> None

    let private describe (DomainError.InvalidTransition(state, command)) =
        let name =
            match command with
            | Submit -> "Submit"
            | Approve _ -> "Approve"
            | Reject _ -> "Reject"
            | Revise -> "Revise"

        $"{name} is not allowed when the request is {Views.stateName state}."

    let private routeId (ctx: HttpContext) =
        match ctx.Request.RouteValues.TryGetValue "id" with
        | true, (:? string as v) -> v
        | _ -> ""

    let private showRequest (store: FileStore) (requestId: string) : IResult =
        if not (validId.IsMatch requestId) then
            html 404 (Views.notFound ())
        else
            match store.Load requestId with
            | Ok r -> html 200 (Views.requestPage r Views.NoBanner)
            | Error _ -> html 404 (Views.notFound ())

    let private create (store: FileStore) (ctx: HttpContext) : Task<IResult> =
        task {
            let! form = ctx.Request.ReadFormAsync()
            let title = field form "title" |> Option.defaultValue ""
            let request = Domain.create (Guid.NewGuid().ToString "N") title

            return
                match store.Save(request, -1) with
                | Ok saved -> seeOther $"/requests/{saved.Id}"
                | Error _ -> html 500 (Views.page "Error" """<p data-testid="error">The request could not be saved.</p>""")
        }

    let private command (store: FileStore) (requestId: string) (ctx: HttpContext) : Task<IResult> =
        task {
            if not (validId.IsMatch requestId) then
                return html 404 (Views.notFound ())
            else
                let! form = ctx.Request.ReadFormAsync()
                let note = field form "note" |> Option.defaultValue ""

                let version =
                    field form "version"
                    |> Option.bind (fun v ->
                        match Int32.TryParse v with
                        | true, n -> Some n
                        | _ -> None)

                let cmd = parseCommand (field form "command") note

                return
                    match store.Load requestId with
                    | Error _ -> html 404 (Views.notFound ())
                    | Ok current ->
                        let errorPage msg =
                            html 422 (Views.requestPage current (Views.ErrorBanner msg))

                        match version, cmd with
                        | None, _ -> errorPage "Missing or invalid version."
                        | _, None -> errorPage "Unknown command."
                        | Some v, _ when v <> current.Version ->
                            html 409 (Views.requestPage current Views.ConflictBanner)
                        | Some v, Some c ->
                            match Domain.decide c current with
                            | Error e -> errorPage (describe e)
                            | Ok next ->
                                match store.Save(next, v) with
                                | Ok _ -> seeOther $"/requests/{requestId}"
                                | Error(Conflict _) ->
                                    let latest = store.Load requestId |> Result.defaultValue current
                                    html 409 (Views.requestPage latest Views.ConflictBanner)
                                | Error _ ->
                                    html 500 (Views.requestPage current (Views.ErrorBanner "The request could not be saved."))
        }

    /// Builds the web application serving the UI, storing in `store`.
    /// `token` is the APPROVAL_TOKEN value: configuration only, never rendered, logged or stored.
    let build (store: FileStore) (token: string) (urls: string) : WebApplication =
        let builder =
            WebApplication.CreateBuilder(WebApplicationOptions(EnvironmentName = Environments.Production))

        builder.WebHost.UseUrls(urls) |> ignore
        // Server header carries nothing useful; drop it.
        builder.WebHost.ConfigureKestrel(fun o -> o.AddServerHeader <- false) |> ignore

        let app = builder.Build()
        let _configuredToken = token

        app.MapGet("/", Func<IResult>(fun () -> html 200 (Views.home ()))) |> ignore

        app.MapPost("/requests", Func<HttpContext, Task<IResult>>(create store))
        |> ignore

        app.MapGet("/requests/{id}", Func<HttpContext, IResult>(fun ctx -> showRequest store (routeId ctx)))
        |> ignore

        app.MapPost("/requests/{id}/command", Func<HttpContext, Task<IResult>>(fun ctx -> command store (routeId ctx) ctx))
        |> ignore

        app
