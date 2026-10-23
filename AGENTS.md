# Working agreement for coding agents

How an agent works in this repository. `CLAUDE.md` says what the app is and the rules of its code; read it first.
Where this file and `CLAUDE.md` disagree about the code, `CLAUDE.md` wins.

## 1. Branching and version control

- **Master first.** Work on `master`. Use another branch only when asked to.
- **Commits that sound human.** When asked to sync, make short commits worded in plain, casual American slang
  (no jargon, no file names), then push without asking again.

## 2. Running, testing and quality

- **Check it starts.** After any code change, close the running app, build, start it again and make sure the window
  opens without an error:
  `dotnet build PoAutoRobo.slnx` then `src\PoAutoRobo.App\bin\Debug\net10.0-windows10.0.19041.0\<rid>\PoAutoRobo.App.exe`.
- **Test only what changed.** Do not run the whole suite. Run the tests for the part you touched with
  `dotnet test PoAutoRobo.slnx --filter "FullyQualifiedName~<Name>"`, or none for a small edit such as wording.
- **No warnings.** Warnings already stop the build (see `Directory.Build.props`); fix each one when it appears,
  never silence it.

## 3. Architecture and configuration

- **Read the docs first.** This repo keeps its architecture in `CLAUDE.md` (there is no spec, `DOCS/` folder or
  `README.md`; if one is added, read it too). When a package, a configuration setting or an environment variable
  changes, update the table in `CLAUDE.md` in the same change.
- **Automate everything you can.** Do not leave the user a command to type or a page to click through when a script,
  build step or test can do it.

## 4. UI changes and reporting

- **Before and after.** For a change to what the window shows, capture the old and the new state, mark what changed,
  save it as `SCREENSHOTS/YYMMDD-feature-name.html` and give its full path. The folder stays on this computer and out
  of the repository (`.gitignore`): its pictures are large and only matter on the day.
- **Build version.** The window shows the build version, `YYMMDD commit` (a `+` after the commit means changes not
  yet committed), in its bottom-right corner. It is stamped at build time from the last commit (today's date alone
  when git is not available); see `StampBuildVersion` in
  `src/PoAutoRobo.App/PoAutoRobo.App.csproj`. Keep it there.
- **TL;DR.** End any reply longer than 100 words with a TL;DR of about 20 words.
- **Three-level summary.** After a code change of more than 20 lines, add three lines, one each:
  1. Toddler: the idea in one simple sentence.
  2. Child: what it now does, in one sentence.
  3. Adult: how it is built, in one technical sentence.
- **Questions about direction.** After a code change of more than 100 lines, when the prompt asked nothing else,
  ask three optional, high-level questions about where the app is going that could lead to further changes.
