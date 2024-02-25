{
  inputs.nixpkgs.url = "github:nixos/nixpkgs/nixos-unstable";
  inputs.flake-utils.url = "github:numtide/flake-utils";

  outputs = { self, nixpkgs, flake-utils }:
      let
        pkgs = nixpkgs.legacyPackages.x86_64-linux;
      in
      {
         packages.x86_64-linux = {
            default = pkgs.buildDotnetModule rec {
              pname = "MatrixMediaGate-v${version}";
              version = "1";
              dotnet-sdk = pkgs.dotnet-sdk_8;
              dotnet-runtime = pkgs.dotnet-aspnetcore_8;
              selfContainedBuild = true;
              src = ./.;
              projectFile = [
                "MatrixMediaGate/MatrixMediaGate.csproj"
               ];
              nugetDeps = MatrixMediaGate/deps.nix;
              nativeBuildInputs = with pkgs; [
                  gcc
              ];
              meta = {
                mainProgram = "MatrixMediaGate";
              };
            };
        };
    };
}
