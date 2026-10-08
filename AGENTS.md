# Agent rules

## Git

- Only use the `master` branch for all work. Use other branches only if specifically asked to.
- When a git sync happens, create a commit whose message is short, uses American slang and is not technical, so it reads as if a human wrote it. Also push the code.

## Working on code

- Always restart the app after making a code change, and verify it restarts successfully.
- Read `SPEC.md` in the root for an overall summary of the project; older planning notes are in `docs`.
- Do not use `dotnet user-secrets` to store data locally. Put it in `appSettings` or in Azure Key Vault (if one exists).
- Treat compile warnings as errors and make sure they are fixed.
- Do not run all tests after code changes. Only run the tests related to the code change, or run no tests at all if the change is simple.
- Avoid making me manually type commands into the CLI or work through a web GUI if you can do it for me automatically.

## Reporting

- At the end of any answer longer than 100 words, add a TLDR summary of 20 words.
- If more than 100 lines of code are removed overall in a prompt, mention it.
- When a change to the UI is made, take an annotated screenshot showing the new and old UI and annotate the changes. Place the image in the `SCREENSHOTS` folder in an HTML file and give the valid full path.
