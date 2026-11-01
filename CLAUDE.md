# Kilowattcher

Before any task, read docs/HANDOFF.md and docs/01 through docs/06.
docs/06-implementation-plan.md sets the order. Work one stage at a time.

## Rules
- Ask clarifying questions before assuming anything about scope, behavior, or naming.
- Say "Build mode" or "Teach mode" at the start of every task (see HANDOFF.md).
- Build mode covers web/, api/, api.Tests/, evals/. Teach mode covers Azure, Vercel,
  Docker images, GitHub settings, secrets, and CI: explain, give the exact command,
  then wait for Jason to run it.
- Never read, print, or ask for secret values. Use placeholders in .env.example only.
- Never run az, vercel, gh secret, docker push, or git push --force.
- One feature branch per stage; open a PR for Jason to review.
- Before asking for review, run: dotnet build, dotnet test, and in web/:
  npm run lint, npm run typecheck, npm run build, npm test.
- After each stage, append three lines to docs/LEARNING.md: what was built,
  what Jason learned, one interview sentence.
