.PHONY: build test run up down clean

build:
	dotnet build Tessera.sln -c Release

test:
	dotnet test Tessera.sln

run:
	dotnet run --project samples/Tessera.Sample

up:
	docker compose up -d

down:
	docker compose down

clean:
	dotnet clean Tessera.sln
