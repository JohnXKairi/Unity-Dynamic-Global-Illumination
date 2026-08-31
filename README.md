# Approximate Dynamic Global Illumination in Unity

University project implementing and extending an approximate dynamic global illumination technique based on **Virtual Point Lights (VPLs)** in Unity.

The project is based on the paper *Approximate Dynamic Global Illumination for VR* by **Georgios Papaioannou** and the original Unity implementation provided by the author.

## My Contributions

The original implementation was modified and extended with:

* Support for multiple dynamic Virtual Point Lights (VPLs)
* Circular distribution of additional dynamic VPLs around the first VPL
* Cosine-weighted hemisphere sampling for secondary VPL generation
* Adjustable number of dynamic and secondary VPLs
* Adjustable intensity for dynamic and secondary VPLs
* Custom Unity scenes for testing and visual evaluation
* Code refactoring and additional comments for improved readability

## Project

The implementation demonstrates a lightweight approximation of indirect diffuse lighting intended for real-time applications, with a particular focus on VR and performance-constrained environments.

## Based On

Georgios Papaioannou, *Approximate Dynamic Global Illumination for VR*, Computer Graphics Forum, 2021.

The original implementation can be found in the author's repository:

[https://github.com/graphicore/Papaioannou-FakeGI-VR](https://github.com/cgaueb/fakeIR?utm_source=chatgpt.com)

## Author

**Ioannis Vasilopoulos**
Athens University of Economics and Business
Department of Computer Science

This project was developed as part of a university project in 2025.
