// Command wirehq-agent is the outbound-only mTLS WireHQ agent.
//
//	wirehq-agent enroll --server https://wirehq.example.com:28443 --token <token>
//	wirehq-agent run    --server https://wirehq.example.com:28443
//
// It enrols once (storing a client cert + the org CA under --state), then loops: pulling signed deployment
// jobs over mTLS, verifying their signature, applying WireGuard config, and reporting status + telemetry.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/WireHQ-CE/wirehq-ce/agent/internal/agent"
)

const defaultStateDir = "/var/lib/wirehq-agent"

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}

	switch os.Args[1] {
	case "enroll":
		enrollCommand(os.Args[2:])
	case "run":
		runCommand(os.Args[2:])
	case "-h", "--help", "help":
		usage()
	default:
		fmt.Fprintf(os.Stderr, "unknown command %q\n\n", os.Args[1])
		usage()
		os.Exit(2)
	}
}

func enrollCommand(args []string) {
	fs := flag.NewFlagSet("enroll", flag.ExitOnError)
	server := fs.String("server", "", "WireHQ agent gateway base URL (https://host:28443)")
	token := fs.String("token", "", "single-use enrollment token (from the Agents tab)")
	name := fs.String("name", "", "a name for this agent (optional)")
	stateDir := fs.String("state", defaultStateDir, "directory for the agent's identity")
	insecure := fs.Bool("insecure", false, "skip server TLS verification (DEVELOPMENT ONLY)")
	_ = fs.Parse(args)

	if *server == "" || *token == "" {
		fatal("enroll: --server and --token are required")
	}

	cfg := agent.Config{Server: *server, StateDir: *stateDir, InsecureSkipVerify: *insecure}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()

	id, err := agent.Enroll(ctx, cfg, *token, *name)
	if err != nil {
		fatal("enroll: %v", err)
	}
	fmt.Printf("enrolled as agent %s\n", id)
}

func runCommand(args []string) {
	fs := flag.NewFlagSet("run", flag.ExitOnError)
	server := fs.String("server", "", "WireHQ agent gateway base URL (https://host:28443)")
	stateDir := fs.String("state", defaultStateDir, "directory for the agent's identity")
	interval := fs.Duration("interval", 30*time.Second, "poll interval")
	once := fs.Bool("once", false, "run a single poll cycle then exit (cron-style scheduling / tests)")
	insecure := fs.Bool("insecure", false, "skip server TLS verification (DEVELOPMENT ONLY)")
	_ = fs.Parse(args)

	if *server == "" {
		fatal("run: --server is required")
	}

	cfg := agent.Config{Server: *server, StateDir: *stateDir, InsecureSkipVerify: *insecure}
	loop, err := agent.NewLoop(cfg)
	if err != nil {
		fatal("run: %v", err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if *once {
		loop.Once(ctx)
		return
	}

	fmt.Printf("wirehq-agent %s polling %s every %s\n", agent.Version, *server, *interval)
	if err := loop.Run(ctx, *interval); err != nil && ctx.Err() == nil {
		fatal("run: %v", err)
	}
}

func usage() {
	fmt.Fprint(os.Stderr, `wirehq-agent — the outbound-only mTLS WireHQ agent

Usage:
  wirehq-agent enroll --server <url> --token <token> [--name <name>] [--state <dir>] [--insecure]
  wirehq-agent run    --server <url> [--state <dir>] [--interval 30s] [--once] [--insecure]
`)
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	os.Exit(1)
}
