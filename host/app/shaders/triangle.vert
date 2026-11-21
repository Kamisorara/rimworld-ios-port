#version 450
// The M2 triangle: the vertex buffer carries position and color; vertex 0 is the one a finger drags.
layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 0) out vec3 vColor;
void main()
{
    vColor = inColor;
    gl_Position = vec4(inPosition, 0.0, 1.0);
}
